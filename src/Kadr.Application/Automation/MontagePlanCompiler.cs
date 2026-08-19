using System.Collections.Immutable;
using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Automation;

public sealed class MontagePlanCompiler(IMontagePlanValidator? validator = null) : IMontagePlanCompiler
{
    private readonly IMontagePlanValidator _validator = validator ?? new MontagePlanValidator();

    public MontageDraftCompilation Compile(
        ProjectState project,
        MontagePlan plan,
        IReadOnlyDictionary<Guid, MediaAnalysisManifest>? manifests = null)
    {
        var validation = _validator.Validate(project, plan);
        if (!validation.IsValid)
            throw new InvalidOperationException(
                "План не прошёл проверку: " +
                string.Join("; ", validation.Validation.Errors.Select(item => item.Message)));

        var settings = plan.TargetFormat switch
        {
            MontageTargetFormat.Shorts => new SequenceSettings(1080, 1920, project.FrameRate, project.Sequence.AudioSampleRate),
            MontageTargetFormat.YouTube => new SequenceSettings(1920, 1080, project.FrameRate, project.Sequence.AudioSampleRate),
            _ => project.Sequence
        };
        var tracks = CreateTracks();
        var visualTrack = tracks.Single(item => item.Kind == TrackKind.Visual && item.Index == 0);
        var audioTrack = tracks.Single(item => item.Kind == TrackKind.Audio && item.Index == 0);
        var textTrack = tracks.Single(item => item.Kind == TrackKind.Text && item.Index == 0);
        var media = ImmutableArray.CreateBuilder<MediaClip>();
        var markers = ImmutableArray.CreateBuilder<TimelineMarker>();
        var text = ImmutableArray.CreateBuilder<TextClip>();
        var placements = new List<Placement>();
        var cursor = TimelineTime.Zero;

        foreach (var item in plan.Items.OrderBy(item => item.Order).ThenBy(item => item.Id))
        {
            var source = project.Sources[item.SourceId];
            var linkGroup = source.HasAudio && source.Kind == MediaKind.Video ? Guid.NewGuid() : (Guid?)null;
            var visualId = Guid.NewGuid();
            var video = item.Reframe ?? CreateDefaultReframe(source, settings, plan.TargetFormat);
            var visual = new MediaClip(
                visualId,
                source.Id,
                visualTrack.Id,
                cursor,
                item.SourceRange.Start,
                item.SourceRange.Duration,
                linkGroup,
                video,
                null);
            media.Add(visual);

            MediaClip? audio = null;
            if (source.HasAudio && source.Kind == MediaKind.Video)
            {
                audio = new MediaClip(
                    Guid.NewGuid(),
                    source.Id,
                    audioTrack.Id,
                    cursor,
                    item.SourceRange.Start,
                    item.SourceRange.Duration,
                    linkGroup,
                    null,
                    new AudioParameters(Volume: item.Volume));
                media.Add(audio);
            }

            markers.Add(new TimelineMarker(
                Guid.NewGuid(),
                MarkerKind.Note,
                cursor,
                item.SourceRange.Duration,
                $"{RoleTitle(item.Role)}: {source.Name}",
                item.Reason,
                source.Id,
                item.SourceRange.Start,
                item.Confidence,
                "ai-montage"));

            if (item.IncludeSubtitles && manifests is not null && manifests.TryGetValue(source.Id, out var manifest))
                AddSubtitles(text, textTrack.Id, item, cursor, manifest, plan.TargetFormat);

            placements.Add(new Placement(item, source, visual, audio));
            cursor += item.SourceRange.Duration;
        }

        var transitions = BuildTransitions(placements, plan.TargetFormat);
        var sequence = new SequenceState(
            Guid.NewGuid(),
            string.IsNullOrWhiteSpace(plan.Title) ? FormatTitle(plan.TargetFormat) : plan.Title.Trim(),
            0,
            SequenceStatus.Draft,
            plan.TargetFormat,
            settings,
            tracks,
            media.ToImmutable(),
            NormalizeSubtitleOverlaps(text.ToImmutable()),
            transitions,
            markers.ToImmutable(),
            ParentSequenceId: plan.Dependencies.InputSequenceId ?? project.ActiveSequenceId,
            MontagePlanId: plan.Id);

        return new MontageDraftCompilation(sequence, validation.Warnings);
    }

    private static ImmutableArray<TimelineTrack> CreateTracks()
        =>
        [
            new TimelineTrack(Guid.NewGuid(), TrackKind.Visual, 0, "V1"),
            new TimelineTrack(Guid.NewGuid(), TrackKind.Visual, 1, "V2"),
            new TimelineTrack(Guid.NewGuid(), TrackKind.Audio, 0, "A1"),
            new TimelineTrack(Guid.NewGuid(), TrackKind.Audio, 1, "A2"),
            new TimelineTrack(Guid.NewGuid(), TrackKind.Text, 0, "T1")
        ];

    private static VideoParameters CreateDefaultReframe(
        MediaSource source,
        SequenceSettings settings,
        MontageTargetFormat format)
    {
        if (format != MontageTargetFormat.Shorts || source.Width <= 0 || source.Height <= 0)
            return new VideoParameters();
        var sourceAspect = source.Width / (double)source.Height;
        var targetAspect = settings.CanvasWidth / (double)settings.CanvasHeight;
        if (sourceAspect > targetAspect)
        {
            var keep = Math.Clamp(targetAspect / sourceAspect, 0.01, 1);
            var crop = (1 - keep) / 2;
            return new VideoParameters(CropLeft: crop, CropRight: crop);
        }
        if (sourceAspect < targetAspect)
        {
            var keep = Math.Clamp(sourceAspect / targetAspect, 0.01, 1);
            var crop = (1 - keep) / 2;
            return new VideoParameters(CropTop: crop, CropBottom: crop);
        }
        return new VideoParameters();
    }

    private static ImmutableArray<TimelineTransition> BuildTransitions(
        IReadOnlyList<Placement> placements,
        MontageTargetFormat format)
    {
        var transitions = ImmutableArray.CreateBuilder<TimelineTransition>();
        var desired = TimelineTime.FromSeconds(format == MontageTargetFormat.Shorts ? 0.14 : 0.3);
        var half = new TimelineTime(desired.Ticks / 2);
        for (var index = 0; index < placements.Count - 1; index++)
        {
            var from = placements[index];
            var to = placements[index + 1];
            if (from.Item.TransitionAfter is not { } kind) continue;
            if (from.Source.Kind != MediaKind.Image && from.Visual.SourceIn + from.Visual.Duration + half > from.Source.Duration)
                continue;
            if (to.Source.Kind != MediaKind.Image && to.Visual.SourceIn < half)
                continue;
            if (desired > from.Visual.Duration || desired > to.Visual.Duration)
                continue;

            transitions.Add(new TimelineTransition(
                Guid.NewGuid(), kind, from.Visual.TrackId, from.Visual.Id, to.Visual.Id,
                from.Visual.End - half, desired));
            if (from.Audio is not null && to.Audio is not null)
                transitions.Add(new TimelineTransition(
                    Guid.NewGuid(), TransitionKind.ConstantPowerAudio, from.Audio.TrackId,
                    from.Audio.Id, to.Audio.Id, from.Audio.End - half, desired));
        }
        return transitions.ToImmutable();
    }

    private static void AddSubtitles(
        ICollection<TextClip> output,
        Guid textTrackId,
        MontagePlanItem item,
        TimelineTime timelineStart,
        MediaAnalysisManifest manifest,
        MontageTargetFormat format)
    {
        foreach (var segment in manifest.Segments
                     .Where(segment => !string.IsNullOrWhiteSpace(segment.Transcript) &&
                                       Intersects(segment.SourceRange, item.SourceRange))
                     .OrderBy(segment => segment.SourceRange.Start))
        {
            var sourceStart = segment.SourceRange.Start >= item.SourceRange.Start
                ? segment.SourceRange.Start
                : item.SourceRange.Start;
            var sourceEnd = segment.SourceRange.End <= item.SourceRange.End
                ? segment.SourceRange.End
                : item.SourceRange.End;
            if (sourceEnd <= sourceStart) continue;
            output.Add(new TextClip(
                Guid.NewGuid(),
                textTrackId,
                timelineStart + (sourceStart - item.SourceRange.Start),
                sourceEnd - sourceStart,
                segment.Transcript.Trim(),
                new TextStyle(
                    FontSize: format == MontageTargetFormat.Shorts ? 64 : 48,
                    Y: format == MontageTargetFormat.Shorts ? 0.76 : 0.84,
                    BoxWidth: format == MontageTargetFormat.Shorts ? 0.86 : 0.7,
                    IsSubtitle: true)));
        }
    }

    private static ImmutableArray<TextClip> NormalizeSubtitleOverlaps(ImmutableArray<TextClip> clips)
    {
        if (clips.IsDefaultOrEmpty) return [];
        var ordered = clips.OrderBy(item => item.Start).ThenBy(item => item.Id).ToArray();
        var result = ImmutableArray.CreateBuilder<TextClip>();
        for (var index = 0; index < ordered.Length; index++)
        {
            var clip = ordered[index];
            if (result.Count > 0 && clip.Start < result[^1].End)
            {
                var previous = result[^1];
                if (clip.Start > previous.Start)
                    result[^1] = previous with { Duration = clip.Start - previous.Start };
                else
                    clip = clip with { Start = previous.End };
            }
            if (clip.Duration > TimelineTime.Zero) result.Add(clip);
        }
        return result.ToImmutable();
    }

    private static string RoleTitle(MontageRole role) => role switch
    {
        MontageRole.Opening => "Опенинг",
        MontageRole.Hook => "Хук",
        MontageRole.Setup => "Завязка",
        MontageRole.Development => "Развитие",
        MontageRole.Payoff => "Кульминация",
        MontageRole.Ending => "Финал",
        _ => role.ToString()
    };

    private static string FormatTitle(MontageTargetFormat format) => format switch
    {
        MontageTargetFormat.Shorts => "Shorts — черновик ИИ",
        MontageTargetFormat.YouTube => "YouTube — черновик ИИ",
        _ => "Черновик ИИ"
    };

    private static bool Intersects(TimeRange left, TimeRange right)
        => left.Start < right.End && left.End > right.Start;

    private sealed record Placement(
        MontagePlanItem Item,
        MediaSource Source,
        MediaClip Visual,
        MediaClip? Audio);
}
