using System.Collections.Concurrent;
using System.Diagnostics;

namespace SwingLoop.Capture;

/// <summary>One monotonic microsecond clock for every camera and microphone (QPC).</summary>
public static class Clock
{
    private static readonly double UsPerTick = 1_000_000.0 / Stopwatch.Frequency;

    public static long NowUs() => (long)(Stopwatch.GetTimestamp() * UsPerTick);

    /// <summary>MediaFrameReference.SystemRelativeTime is QPC time in 100 ns units.</summary>
    public static long FromSystemRelative(TimeSpan t) => t.Ticks / 10;
}

/// <summary>
/// A tightly packed NV12 frame (Y plane, then interleaved UV at half resolution).
/// Reference counted so the ring buffer and the clip writer can share it
/// without copying; the last Release returns the buffer to its pool.
/// </summary>
public sealed class VideoFrame
{
    private readonly FramePool _pool;
    private int _refs = 1;

    internal VideoFrame(FramePool pool, byte[] data, int width, int height)
    {
        _pool = pool;
        Data = data;
        Width = width;
        Height = height;
    }

    public byte[] Data { get; }
    public int Width { get; }
    public int Height { get; }
    public long TimestampUs { get; internal set; }
    public int Length => Width * Height * 3 / 2;

    public VideoFrame Retain()
    {
        Interlocked.Increment(ref _refs);
        return this;
    }

    public void Release()
    {
        int left = Interlocked.Decrement(ref _refs);
        if (left == 0) _pool.Return(this);
        Debug.Assert(left >= 0, "VideoFrame released too many times");
    }

    internal void Revive() => _refs = 1;
}

public sealed class FramePool
{
    private readonly ConcurrentBag<VideoFrame> _free = new();
    private int _width, _height;

    public VideoFrame Rent(int width, int height, long timestampUs)
    {
        if (width != _width || height != _height)
        {
            _free.Clear();
            _width = width;
            _height = height;
        }
        if (!_free.TryTake(out var frame) || frame.Width != width || frame.Height != height)
            frame = new VideoFrame(this, new byte[width * height * 3 / 2], width, height);
        frame.Revive();
        frame.TimestampUs = timestampUs;
        return frame;
    }

    internal void Return(VideoFrame f)
    {
        if (f.Width == _width && f.Height == _height && _free.Count < 32) _free.Add(f);
    }
}

/// <summary>
/// Rolling in-memory history of the last few seconds of video. A swing is
/// detected after impact, so the clip is cut from here, pre-roll included.
/// </summary>
public sealed class FrameRing
{
    private readonly object _lock = new();
    private readonly Queue<VideoFrame> _frames = new();
    private long _bytes;

    public long MaxBytes { get; set; } = 1200L * 1024 * 1024;
    public long MaxDurationUs { get; set; } = 6_000_000;

    public long NewestTimestampUs { get; private set; } = long.MinValue;

    public long OldestTimestampUs
    {
        get { lock (_lock) return _frames.Count > 0 ? _frames.Peek().TimestampUs : long.MinValue; }
    }

    /// <summary>Takes ownership of one reference to <paramref name="frame"/>.</summary>
    public void Add(VideoFrame frame)
    {
        lock (_lock)
        {
            _frames.Enqueue(frame);
            _bytes += frame.Length;
            NewestTimestampUs = frame.TimestampUs;
            while (_frames.Count > 1 &&
                   (_bytes > MaxBytes || frame.TimestampUs - _frames.Peek().TimestampUs > MaxDurationUs))
            {
                var old = _frames.Dequeue();
                _bytes -= old.Length;
                old.Release();
            }
        }
    }

    /// <summary>Frames in [fromUs, toUs], each retained: the caller must Release them.</summary>
    public List<VideoFrame> Snapshot(long fromUs, long toUs)
    {
        lock (_lock)
            return _frames.Where(f => f.TimestampUs >= fromUs && f.TimestampUs <= toUs).Select(f => f.Retain()).ToList();
    }

    public void Clear()
    {
        lock (_lock)
        {
            while (_frames.Count > 0) _frames.Dequeue().Release();
            _bytes = 0;
            NewestTimestampUs = long.MinValue;
        }
    }
}
