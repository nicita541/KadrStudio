namespace KadrStudio.Application.Preview;

public sealed record SharedFrameDescriptor(
    long FrameId,
    int SlotIndex,
    long Generation,
    long TimestampTicks,
    int Width,
    int Height,
    int Stride,
    int ValidLength);

public interface ISharedFrameReadLease : IDisposable
{
    SharedFrameDescriptor Descriptor { get; }
    long FrameId { get; }
    int ValidLength { get; }
    void CopyTo(byte[] destination);
}

public sealed class LatestValueDispatcher<T>(Action<Action> schedule, Action<T> present)
{
    private readonly object _gate = new();
    private T? _latest;
    private bool _hasLatest;
    private bool _pending;
    private long _droppedCount;

    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    public void Offer(T value)
    {
        var shouldSchedule = false;
        lock (_gate)
        {
            if (_hasLatest) Interlocked.Increment(ref _droppedCount);
            _latest = value;
            _hasLatest = true;
            if (!_pending)
            {
                _pending = true;
                shouldSchedule = true;
            }
        }
        if (shouldSchedule) schedule(Drain);
    }

    private void Drain()
    {
        while (true)
        {
            T value;
            lock (_gate)
            {
                if (!_hasLatest)
                {
                    _pending = false;
                    return;
                }
                value = _latest!;
                _latest = default;
                _hasLatest = false;
            }
            present(value);
        }
    }
}

public sealed class LatestVideoFrameDispatcher(Action<Action> schedule, Action<VideoFrame> present,
    Func<VideoFrame, bool>? isCurrent = null)
{
    private readonly object _gate = new();
    private readonly FrameBuffer[] _buffers = [new(), new(), new()];
    private int _latest = -1;
    private bool _pending;
    private long _droppedCount;
    private long _allocatedBytes;
    private long _copiedBytes;

    public long DroppedCount => Interlocked.Read(ref _droppedCount);
    public long AllocatedBytes => Interlocked.Read(ref _allocatedBytes);
    public long CopiedBytes => Interlocked.Read(ref _copiedBytes);

    public void Clear()
    {
        lock (_gate)
        {
            if (_latest >= 0)
            {
                _buffers[_latest].Frame = null;
                _latest = -1;
                Interlocked.Increment(ref _droppedCount);
            }
        }
    }

    public void Offer(VideoFrame frame)
    {
        var shouldSchedule = false;
        lock (_gate)
        {
            var target = _latest;
            if (target >= 0)
            {
                Interlocked.Increment(ref _droppedCount);
            }
            else
            {
                target = Array.FindIndex(_buffers, buffer => !buffer.Presenting);
                if (target < 0)
                {
                    Interlocked.Increment(ref _droppedCount);
                    return;
                }
            }
            var buffer = _buffers[target];
            if (buffer.Pixels.Length < frame.Bgra.Length)
            {
                var replacement = new byte[frame.Bgra.Length];
                Interlocked.Add(ref _allocatedBytes, replacement.Length);
                buffer.Pixels = replacement;
            }
            frame.Bgra.Span.CopyTo(buffer.Pixels);
            Interlocked.Add(ref _copiedBytes, frame.Bgra.Length);
            buffer.Frame = frame with { Bgra = buffer.Pixels.AsMemory(0, frame.Bgra.Length) };
            _latest = target;
            if (!_pending)
            {
                _pending = true;
                shouldSchedule = true;
            }
        }
        if (shouldSchedule) schedule(Drain);
    }

    private void Drain()
    {
        while (true)
        {
            int index;
            VideoFrame frame;
            lock (_gate)
            {
                if (_latest < 0)
                {
                    _pending = false;
                    return;
                }
                index = _latest;
                _latest = -1;
                _buffers[index].Presenting = true;
                frame = _buffers[index].Frame!;
            }
            try
            {
                if (isCurrent?.Invoke(frame) != false) present(frame);
                else Interlocked.Increment(ref _droppedCount);
            }
            finally
            {
                lock (_gate) _buffers[index].Presenting = false;
            }
        }
    }

    private sealed class FrameBuffer
    {
        public byte[] Pixels { get; set; } = [];
        public VideoFrame? Frame { get; set; }
        public bool Presenting { get; set; }
    }
}
