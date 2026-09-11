using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using KadrStudio.Application.Preview;

namespace KadrStudio.Infrastructure.Preview;

public sealed class SharedFrameRingWriter : IDisposable
{
    private const int GlobalHeaderBytes = 64;
    private const int SlotHeaderBytes = 64;
    private const int Magic = 0x4B46524D;
    private const int Ready = 2;
    private const int Reading = 3;
    private readonly int _slotCapacity;
    private readonly int _slotCount;
    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private readonly Mutex _mutex;
    private long _nextFrameId;
    private int _nextSlot;

    private SharedFrameRingWriter(string name, int slotCapacity, int slotCount)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Named shared frame buffers require Windows.");
        _slotCapacity = slotCapacity;
        _slotCount = slotCount;
        var capacity = checked(GlobalHeaderBytes + (long)slotCount * (SlotHeaderBytes + slotCapacity));
        _map = MemoryMappedFile.CreateNew(name, capacity, MemoryMappedFileAccess.ReadWrite);
        _view = _map.CreateViewAccessor(0, capacity, MemoryMappedFileAccess.ReadWrite);
        _mutex = new Mutex(false, name + "-mutex");
        WithLock(() =>
        {
            _view.Write(0, Magic);
            _view.Write(4, MediaHostProtocol.Version);
            _view.Write(8, slotCount);
            _view.Write(12, slotCapacity);
            for (var index = 0; index < slotCount; index++) _view.Write(SlotOffset(index), 0);
        });
    }

    public static SharedFrameRingWriter Create(string name, int slotCapacity, int slotCount = 3)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A map name is required.", nameof(name));
        if (slotCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(slotCapacity));
        if (slotCount != 3) throw new ArgumentOutOfRangeException(nameof(slotCount), "The preview ring must have three slots.");
        return new SharedFrameRingWriter(name, slotCapacity, slotCount);
    }

    public bool TryWrite(VideoFrame frame, out SharedFrameDescriptor descriptor)
    {
        var validLength = frame.Bgra.Length;
        if (validLength <= 0 || validLength > _slotCapacity)
            throw new InvalidDataException($"BGRA frame length {validLength} exceeds slot capacity {_slotCapacity}.");
        SharedFrameDescriptor? written = null;
        WithLock(() =>
        {
            var slot = FindWritableSlot();
            if (slot < 0) return;
            _nextSlot = (slot + 1) % _slotCount;
            var offset = SlotOffset(slot);
            _view.Write(offset, 1);
            var frameId = ++_nextFrameId;
            _view.Write(offset + 4, validLength);
            _view.Write(offset + 8, frame.Width);
            _view.Write(offset + 12, frame.Height);
            _view.Write(offset + 16, frame.Stride);
            _view.Write(offset + 24, frame.Generation);
            _view.Write(offset + 32, frame.Position.Ticks);
            _view.Write(offset + 40, frameId);
            if (MemoryMarshal.TryGetArray(frame.Bgra, out ArraySegment<byte> pixels) && pixels.Array is not null)
            {
                _view.WriteArray(offset + SlotHeaderBytes, pixels.Array, pixels.Offset, validLength);
            }
            else
            {
                var rented = ArrayPool<byte>.Shared.Rent(validLength);
                try
                {
                    frame.Bgra.Span.CopyTo(rented);
                    _view.WriteArray(offset + SlotHeaderBytes, rented, 0, validLength);
                }
                finally { ArrayPool<byte>.Shared.Return(rented); }
            }
            _view.Write(offset, Ready);
            _view.Write(16, frameId);
            written = new SharedFrameDescriptor(
                frameId, slot, frame.Generation, frame.Position.Ticks,
                frame.Width, frame.Height, frame.Stride, validLength);
        });
        descriptor = written ?? new SharedFrameDescriptor(0, -1, 0, 0, 0, 0, 0, 0);
        return written is not null;
    }

    private int FindWritableSlot()
    {
        var oldestReady = -1;
        var oldestFrame = long.MaxValue;
        for (var offsetIndex = 0; offsetIndex < _slotCount; offsetIndex++)
        {
            var index = (_nextSlot + offsetIndex) % _slotCount;
            var offset = SlotOffset(index);
            var state = _view.ReadInt32(offset);
            if (state == 0) return index;
            if (state != Ready && state != Reading && state != 1)
                _view.Write(offset, 0);
            if (state != Ready) continue;
            var frameId = _view.ReadInt64(offset + 40);
            if (frameId >= oldestFrame) continue;
            oldestFrame = frameId;
            oldestReady = index;
        }
        return oldestReady;
    }

    private long SlotOffset(int slot) => GlobalHeaderBytes + (long)slot * (SlotHeaderBytes + _slotCapacity);

    private void WithLock(Action action)
    {
        try { _mutex.WaitOne(); }
        catch (AbandonedMutexException) { }
        try { action(); }
        finally { _mutex.ReleaseMutex(); }
    }

    public void Dispose()
    {
        _view.Dispose();
        _map.Dispose();
        _mutex.Dispose();
    }
}

public sealed class SharedFrameRingReader : IDisposable
{
    private const int GlobalHeaderBytes = 64;
    private const int SlotHeaderBytes = 64;
    private const int Magic = 0x4B46524D;
    private const int Ready = 2;
    private const int Reading = 3;
    private readonly int _slotCapacity;
    private readonly int _slotCount;
    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private readonly Mutex _mutex;
    private long _lastAcquiredFrameId;

    private SharedFrameRingReader(string name, int slotCapacity, int slotCount)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Named shared frame buffers require Windows.");
        _slotCapacity = slotCapacity;
        _slotCount = slotCount;
        _map = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite);
        var capacity = checked(GlobalHeaderBytes + (long)slotCount * (SlotHeaderBytes + slotCapacity));
        _view = _map.CreateViewAccessor(0, capacity, MemoryMappedFileAccess.ReadWrite);
        _mutex = Mutex.OpenExisting(name + "-mutex");
        if (_view.ReadInt32(0) != Magic || _view.ReadInt32(4) != MediaHostProtocol.Version ||
            _view.ReadInt32(8) != slotCount || _view.ReadInt32(12) != slotCapacity)
            throw new InvalidDataException("Shared preview frame buffer header is incompatible.");
    }

    public static SharedFrameRingReader Open(string name, int slotCapacity, int slotCount = 3)
        => new(name, slotCapacity, slotCount);

    public SharedFrameReadLease? TryAcquireLatest()
    {
        SharedFrameReadLease? lease = null;
        WithLock(() =>
        {
            var selected = -1;
            var newest = _lastAcquiredFrameId;
            for (var index = 0; index < _slotCount; index++)
            {
                var offset = SlotOffset(index);
                if (_view.ReadInt32(offset) != Ready) continue;
                var frameId = _view.ReadInt64(offset + 40);
                if (frameId <= _lastAcquiredFrameId)
                {
                    _view.Write(offset, 0);
                    continue;
                }
                if (frameId <= newest) continue;
                newest = frameId;
                selected = index;
            }
            if (selected < 0) return;
            _lastAcquiredFrameId = newest;
            for (var index = 0; index < _slotCount; index++)
            {
                var offset = SlotOffset(index);
                if (index != selected && _view.ReadInt32(offset) == Ready &&
                    _view.ReadInt64(offset + 40) <= newest)
                    _view.Write(offset, 0);
            }
            var selectedOffset = SlotOffset(selected);
            _view.Write(selectedOffset, Reading);
            lease = new SharedFrameReadLease(
                this,
                new SharedFrameDescriptor(
                    _view.ReadInt64(selectedOffset + 40), selected,
                    _view.ReadInt64(selectedOffset + 24), _view.ReadInt64(selectedOffset + 32),
                    _view.ReadInt32(selectedOffset + 8), _view.ReadInt32(selectedOffset + 12),
                    _view.ReadInt32(selectedOffset + 16), _view.ReadInt32(selectedOffset + 4)));
        });
        return lease;
    }

    internal void Copy(int slot, byte[] destination, int length)
    {
        if (destination.Length < length) throw new ArgumentException("Destination is too small.", nameof(destination));
        _view.ReadArray(SlotOffset(slot) + SlotHeaderBytes, destination, 0, length);
    }

    internal void Release(int slot)
        => WithLock(() =>
        {
            var offset = SlotOffset(slot);
            if (_view.ReadInt32(offset) == Reading) _view.Write(offset, 0);
        });

    private long SlotOffset(int slot) => GlobalHeaderBytes + (long)slot * (SlotHeaderBytes + _slotCapacity);

    private void WithLock(Action action)
    {
        try { _mutex.WaitOne(); }
        catch (AbandonedMutexException) { }
        try { action(); }
        finally { _mutex.ReleaseMutex(); }
    }

    public void Dispose()
    {
        _view.Dispose();
        _map.Dispose();
        _mutex.Dispose();
    }
}

public sealed class SharedFrameReadLease : ISharedFrameReadLease
{
    private SharedFrameRingReader? _owner;

    internal SharedFrameReadLease(SharedFrameRingReader owner, SharedFrameDescriptor descriptor)
    {
        _owner = owner;
        Descriptor = descriptor;
    }

    public SharedFrameDescriptor Descriptor { get; }
    public long FrameId => Descriptor.FrameId;
    public int ValidLength => Descriptor.ValidLength;
    public void CopyTo(byte[] destination) => (_owner ?? throw new ObjectDisposedException(nameof(SharedFrameReadLease)))
        .Copy(Descriptor.SlotIndex, destination, Descriptor.ValidLength);

    public void Dispose()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        owner?.Release(Descriptor.SlotIndex);
    }
}

