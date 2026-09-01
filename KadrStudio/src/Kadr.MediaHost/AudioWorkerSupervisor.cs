using System.Collections.Immutable;
using System.Diagnostics;
using KadrStudio.Application.Preview;
using KadrStudio.Application.Rendering;
using KadrStudio.Core.Domain;
using KadrStudio.Infrastructure.Rendering;

namespace KadrStudio.MediaHost;

/// <summary>
/// Decodes each active audio clip independently and mixes aligned stereo
/// float32 blocks. Failed layers become silence without stopping other layers.
/// </summary>
public sealed class AudioWorkerSupervisor(
    string ffmpegPath,
    Action<Exception>? reportFailure = null) : IAsyncDisposable
{
    private const int BlockFrames = 1_024;
    private readonly string _ffmpegPath = Path.GetFullPath(ffmpegPath);
    private readonly FfmpegRenderCommandBuilder _commands = new();
    private readonly Dictionary<Guid, AudioLayerWorker> _workers = [];
    private bool _disposed;

    public int ActiveWorkerCount => _workers.Count;
    public int PeakWorkerCount { get; private set; }
    public long StartedWorkerCount { get; private set; }

    public async Task RunAsync(
        RenderPlan plan,
        TimelineTime start,
        long generation,
        Func<AudioBlock, ValueTask> consume,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(consume);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var processedFrames = 0L;
        var totalFrames = (long)Math.Ceiling((plan.Range.End - start).TotalSeconds * plan.AudioSampleRate);
        while (processedFrames < totalFrames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var position = start + TimelineTime.FromSeconds(processedFrames / (double)plan.AudioSampleRate);
            var frameCount = (int)Math.Min(BlockFrames, totalFrames - processedFrames);
            var blockEnd = start + TimelineTime.FromSeconds(
                (processedFrames + frameCount) / (double)plan.AudioSampleRate);
            var active = plan.AudioLayers
                .Where(layer => ActiveRange(plan, layer).Start < blockEnd && ActiveRange(plan, layer).End > position)
                .OrderBy(layer => layer.TrackIndex)
                .ThenBy(layer => layer.TimelineRange.Start)
                .ThenBy(layer => layer.ClipId)
                .ToArray();
            var activeIds = active.Select(item => item.ClipId).ToHashSet();
            await RetireInactiveAsync(plan, position, activeIds).ConfigureAwait(false);
            var mixed = new float[frameCount * 2];
            foreach (var layer in active)
            {
                float[]? samples = null;
                var layerRange = ActiveRange(plan, layer);
                var segmentStart = position >= layerRange.Start ? position : layerRange.Start;
                var segmentEnd = blockEnd <= layerRange.End ? blockEnd : layerRange.End;
                var destinationFrame = (int)Math.Round(
                    (segmentStart - position).TotalSeconds * plan.AudioSampleRate,
                    MidpointRounding.AwayFromZero);
                var segmentFrames = Math.Min(
                    frameCount - destinationFrame,
                    (int)Math.Round((segmentEnd - segmentStart).TotalSeconds * plan.AudioSampleRate,
                        MidpointRounding.AwayFromZero));
                if (segmentFrames <= 0) continue;
                try
                {
                    var worker = await GetOrCreateAsync(plan, layer, segmentStart, cancellationToken).ConfigureAwait(false);
                    samples = await worker.ReadAsync(segmentFrames, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    reportFailure?.Invoke(new AudioWorkerException(layer.ClipId, exception));
                    if (_workers.Remove(layer.ClipId, out var failed))
                        await failed.DisposeAsync().ConfigureAwait(false);
                }
                if (samples is null) continue;
                var destinationSample = destinationFrame * 2;
                for (var index = 0; index < samples.Length; index++)
                    mixed[destinationSample + index] += samples[index];
            }
            for (var index = 0; index < mixed.Length; index++) mixed[index] = Math.Clamp(mixed[index], -1f, 1f);
            await consume(new AudioBlock(position, plan.AudioSampleRate, 2, mixed, generation)).ConfigureAwait(false);
            processedFrames += frameCount;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        var workers = _workers.Values.ToArray();
        _workers.Clear();
        foreach (var worker in workers) await worker.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<AudioLayerWorker> GetOrCreateAsync(
        RenderPlan plan,
        RenderAudioLayer layer,
        TimelineTime position,
        CancellationToken cancellationToken)
    {
        if (_workers.TryGetValue(layer.ClipId, out var existing)) return existing;
        var activeRange = ActiveRange(plan, layer);
        var end = activeRange.End <= plan.Range.End ? activeRange.End : plan.Range.End;
        if (end <= position) throw new InvalidOperationException("Audio layer has no remaining decode range.");
        var workerPlan = CreateWorkerPlan(plan, layer, new TimeRange(position, end - position));
        var worker = await AudioLayerWorker.StartAsync(
            _ffmpegPath, _commands, workerPlan, cancellationToken).ConfigureAwait(false);
        _workers.Add(layer.ClipId, worker);
        StartedWorkerCount++;
        PeakWorkerCount = Math.Max(PeakWorkerCount, _workers.Count);
        return worker;
    }

    private async Task RetireInactiveAsync(
        RenderPlan plan,
        TimelineTime position,
        IReadOnlySet<Guid> activeIds)
    {
        foreach (var pair in _workers.ToArray())
        {
            var layer = plan.AudioLayers.FirstOrDefault(item => item.ClipId == pair.Key);
            if (layer is not null && (activeIds.Contains(pair.Key) || position < ActiveRange(plan, layer).End)) continue;
            _workers.Remove(pair.Key);
            await pair.Value.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static RenderPlan CreateWorkerPlan(
        RenderPlan plan,
        RenderAudioLayer layer,
        TimeRange range)
    {
        var transitions = plan.AudioTransitions
            .Where(item => (item.From.ClipId == layer.ClipId || item.To.ClipId == layer.ClipId) &&
                           item.TimelineRange.Overlaps(range))
            .ToImmutableArray();
        return plan with
        {
            Range = range,
            VisualLayers = [],
            AudioLayers = [layer],
            TextLayers = [],
            VideoTransitions = [],
            AudioTransitions = transitions
        };
    }

    private static TimeRange ActiveRange(RenderPlan plan, RenderAudioLayer layer)
    {
        var start = layer.TimelineRange.Start;
        var end = layer.TimelineRange.End;
        foreach (var transition in plan.AudioTransitions.Where(item =>
                     item.From.ClipId == layer.ClipId || item.To.ClipId == layer.ClipId))
        {
            if (transition.TimelineRange.Start < start) start = transition.TimelineRange.Start;
            if (transition.TimelineRange.End > end) end = transition.TimelineRange.End;
        }
        return new TimeRange(start, end - start);
    }

    private sealed class AudioLayerWorker : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _errorOutput;
        private bool _disposed;

        private AudioLayerWorker(Process process)
        {
            _process = process;
            _errorOutput = process.StandardError.ReadToEndAsync();
        }

        public static Task<AudioLayerWorker> StartAsync(
            string ffmpegPath,
            FfmpegRenderCommandBuilder commands,
            RenderPlan plan,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var command = commands.Build(plan, new RenderOutputOptions(
                RenderPurpose.AudioServer, "pipe:1", 16, 16,
                IncludeVideo: false, IncludeAudio: true, IncludeOverlays: false));
            var info = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in command.Arguments) info.ArgumentList.Add(argument);
            var process = new Process { StartInfo = info };
            if (!process.Start()) throw new InvalidOperationException("Audio source decoder did not start.");
            return Task.FromResult(new AudioLayerWorker(process));
        }

        public async Task<float[]> ReadAsync(int frameCount, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var byteCount = checked(frameCount * 2 * sizeof(float));
            var bytes = GC.AllocateUninitializedArray<byte>(byteCount);
            var offset = 0;
            while (offset < byteCount)
            {
                var read = await _process.StandardOutput.BaseStream
                    .ReadAsync(bytes.AsMemory(offset), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    if (offset == 0)
                    {
                        var errors = await _errorOutput.ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(errors)) throw new EndOfStreamException(errors);
                    }
                    bytes.AsSpan(offset).Clear();
                    break;
                }
                offset += read;
            }
            return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes).ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
            try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
            _process.Dispose();
        }
    }
}

public sealed class AudioWorkerException(Guid clipId, Exception innerException)
    : Exception($"Audio worker {clipId:N} failed: {innerException.Message}", innerException)
{
    public Guid ClipId { get; } = clipId;
}
