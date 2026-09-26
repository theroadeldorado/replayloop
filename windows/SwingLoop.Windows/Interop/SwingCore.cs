using System.Runtime.InteropServices;

namespace SwingLoop.Interop;

// Mirrors core/include/swingcore/swingcore_c.h. Keep the two in sync.

public enum SwingPhase
{
    NoGolfer = 0,
    Idle = 1,
    Address = 2,
    Backswing = 3,
    Downswing = 4,
    FollowThrough = 5,
    Cooldown = 6,
}

public enum SwingEventType
{
    GolferEntered = 1,
    GolferLeft = 2,
    Address = 3,
    SwingStarted = 4,
    WaggleIgnored = 5,
    PracticeSwing = 6,
    SwingCaptured = 7,
    SwingAborted = 8,
}

public enum CaptureSource
{
    Pose = 0,
    AudioMotion = 1,
    Manual = 2,
}

[StructLayout(LayoutKind.Sequential)]
public struct SwingEvent
{
    public SwingEventType Type;
    public CaptureSource Source;
    public long TimestampUs;
    public long AddressUs;
    public long TakeawayUs;
    public long TopUs;
    public long ImpactUs;
    public long FinishUs;
    public long ClipStartUs;
    public long ClipEndUs;
    public float Confidence;
    public float TempoRatio;
    public int ImpactConfirmed;
    public int Reserved;
}

internal static class Native
{
    private const string Lib = "swingcore";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sc_version();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sc_last_error();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void sc_free(IntPtr p);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr sc_detector_create([MarshalAs(UnmanagedType.LPUTF8Str)] string? configJson);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void sc_detector_destroy(IntPtr d);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sc_detector_configure(IntPtr d, [MarshalAs(UnmanagedType.LPUTF8Str)] string configJson);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void sc_detector_reset(IntPtr d);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe void sc_detector_push_pose(IntPtr d, long tsUs, float* keypoints);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe void sc_detector_push_audio(IntPtr d, long firstSampleUs, float* samples, int count, int sampleRate);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe float sc_detector_push_luma(IntPtr d, long tsUs, byte* luma, int width, int height, int stride);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void sc_detector_tick(IntPtr d, long nowUs);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void sc_detector_trigger_manual(IntPtr d, long nowUs);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int sc_detector_poll(IntPtr d, out SwingEvent e);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int sc_detector_phase(IntPtr d);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int sc_detector_golfer_present(IntPtr d);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void sc_detector_audio_levels(IntPtr d, out float levelDb, out float floorDb);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr sc_session_create([MarshalAs(UnmanagedType.LPUTF8Str)] string root, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr sc_session_open([MarshalAs(UnmanagedType.LPUTF8Str)] string folder);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void sc_session_close(IntPtr s);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sc_session_json(IntPtr s);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sc_session_folder(IntPtr s);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr sc_session_add_shot(IntPtr s, [MarshalAs(UnmanagedType.LPUTF8Str)] string shotJson);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sc_session_update_shot(IntPtr s, [MarshalAs(UnmanagedType.LPUTF8Str)] string shotJson);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sc_session_remove_shot(IntPtr s, [MarshalAs(UnmanagedType.LPUTF8Str)] string shotId, int deleteFiles);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sc_session_set_persistent_annotations(IntPtr s, [MarshalAs(UnmanagedType.LPUTF8Str)] string shapesJson);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sc_session_set_cameras(IntPtr s, [MarshalAs(UnmanagedType.LPUTF8Str)] string camerasJson);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sc_session_rename(IntPtr s, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int sc_session_end(IntPtr s);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr sc_sessions_list([MarshalAs(UnmanagedType.LPUTF8Str)] string root);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe int sc_layout_grid(float width, float height, float* aspects, int n, float gap, float uniformAspect,
        float* outContent, float* outCells, out int rows, out int cols);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe void sc_rotation_crop(float srcW, float srcH, float degrees, float targetAspect, float* out3);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe void sc_zoom_window(float zoom, float cx, float cy, float* out4);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sc_new_id();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sc_annotation_hit_test([MarshalAs(UnmanagedType.LPUTF8Str)] string shapesJson, float x, float y, float tolerance, float aspect);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern float sc_annotation_angle([MarshalAs(UnmanagedType.LPUTF8Str)] string shapeJson, float aspect);

    /// <summary>Copies and frees a string returned by the core.</summary>
    public static string? Take(IntPtr p)
    {
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(p); }
        finally { sc_free(p); }
    }

    public static string LastError => Marshal.PtrToStringUTF8(sc_last_error()) ?? "unknown error";
}

public sealed class SwingCoreException(string message) : Exception(message);

/// <summary>Thread-safe wrapper over the native swing detector.</summary>
public sealed class SwingDetector : IDisposable
{
    private IntPtr _handle;

    public SwingDetector(string? configJson = null)
    {
        _handle = Native.sc_detector_create(configJson);
        if (_handle == IntPtr.Zero) throw new SwingCoreException(Native.LastError);
    }

    public void Configure(string configJson)
    {
        if (Native.sc_detector_configure(_handle, configJson) == 0) throw new SwingCoreException(Native.LastError);
    }

    public void Reset() => Native.sc_detector_reset(_handle);

    public unsafe void PushPose(long tsUs, float[]? keypoints)
    {
        if (keypoints is null) { Native.sc_detector_push_pose(_handle, tsUs, null); return; }
        if (keypoints.Length < 51) throw new ArgumentException("expected 17 x,y,score triplets", nameof(keypoints));
        fixed (float* p = keypoints) Native.sc_detector_push_pose(_handle, tsUs, p);
    }

    public unsafe void PushAudio(long firstSampleUs, ReadOnlySpan<float> samples, int sampleRate)
    {
        fixed (float* p = samples) Native.sc_detector_push_audio(_handle, firstSampleUs, p, samples.Length, sampleRate);
    }

    public unsafe float PushLuma(long tsUs, byte[] luma, int width, int height, int stride)
    {
        fixed (byte* p = luma) return Native.sc_detector_push_luma(_handle, tsUs, p, width, height, stride);
    }

    public void Tick(long nowUs) => Native.sc_detector_tick(_handle, nowUs);
    public void TriggerManual(long nowUs) => Native.sc_detector_trigger_manual(_handle, nowUs);
    public bool Poll(out SwingEvent e) => Native.sc_detector_poll(_handle, out e) != 0;
    public SwingPhase Phase => (SwingPhase)Native.sc_detector_phase(_handle);
    public bool GolferPresent => Native.sc_detector_golfer_present(_handle) != 0;

    public (float LevelDb, float FloorDb) AudioLevels
    {
        get { Native.sc_detector_audio_levels(_handle, out var l, out var f); return (l, f); }
    }

    public void Dispose()
    {
        var h = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (h != IntPtr.Zero) Native.sc_detector_destroy(h);
    }
}

/// <summary>Stateless geometry helpers shared with the phone apps.</summary>
public static class CoreLayout
{
    public readonly record struct Rect(float X, float Y, float W, float H);

    public static unsafe (Rect[] Content, Rect[] Cells, int Rows, int Cols) Grid(
        float width, float height, IReadOnlyList<float> aspects, float gap, float uniformAspect)
    {
        int n = aspects.Count;
        if (n == 0 || width <= 0 || height <= 0) return ([], [], 0, 0);
        var a = aspects.ToArray();
        var content = new float[n * 4];
        var cells = new float[n * 4];
        int rows, cols;
        fixed (float* pa = a, pc = content, pl = cells)
        {
            if (Native.sc_layout_grid(width, height, pa, n, gap, uniformAspect, pc, pl, out rows, out cols) == 0)
                return ([], [], 0, 0);
        }
        static Rect[] ToRects(float[] f) =>
            Enumerable.Range(0, f.Length / 4).Select(i => new Rect(f[i * 4], f[i * 4 + 1], f[i * 4 + 2], f[i * 4 + 3])).ToArray();
        return (ToRects(content), ToRects(cells), rows, cols);
    }

    /// <summary>Extra zoom needed so a frame rotated by <paramref name="degrees"/> shows no empty corners.</summary>
    public static unsafe float RotationZoom(float srcW, float srcH, float degrees)
    {
        if (srcW <= 0 || srcH <= 0 || degrees % 360 == 0) return 1f;
        float* r = stackalloc float[3];
        Native.sc_rotation_crop(srcW, srcH, degrees, srcW / srcH, r);
        return r[2];
    }
}

public static class CoreIds
{
    public static string NewId() => Native.Take(Native.sc_new_id()) ?? Guid.NewGuid().ToString("N")[..16];
    public static string Version => System.Runtime.InteropServices.Marshal.PtrToStringUTF8(Native.sc_version()) ?? "?";
}
