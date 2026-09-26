using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.MediaProperties;
using WinRT;

namespace SwingLoop.Capture;

public sealed record CameraDescriptor(string Id, string Name, string Kind);

public sealed record CaptureFormatPreference(int MaxWidth = 1920, int MaxHeight = 1080, double MinFps = 30);

/// <summary>
/// A live video source: a USB/built-in camera now, a phone over the network
/// later (see docs/CAMERA_PROTOCOL.md). Everything downstream - ring buffer,
/// swing detection, clip writing, preview - only sees this interface.
/// </summary>
public interface ICameraSource : IAsyncDisposable
{
    string Id { get; }
    string Name { get; }
    string Kind { get; }  // "local" | "phone"
    int Width { get; }
    int Height { get; }
    double Fps { get; }
    bool IsRunning { get; }
    FrameRing Ring { get; }

    /// <summary>Raised on a capture thread after the frame is in the ring. Retain it to keep it.</summary>
    event Action<ICameraSource, VideoFrame>? FrameArrived;

    Task StartAsync(CaptureFormatPreference pref);
    Task StopAsync();

    /// <summary>A GPU-backed live source for MediaPlayerElement previews.</summary>
    MediaSource? CreatePreviewSource();

    bool SupportsHardwareZoom { get; }
    float MinZoom { get; }
    float MaxZoom { get; }
    Task SetHardwareZoomAsync(float zoom);
}

public sealed class LocalCameraSource : ICameraSource
{
    private readonly string _groupId;
    private readonly FramePool _pool = new();
    private MediaCapture? _capture;
    private MediaFrameSource? _source;
    private MediaFrameReader? _reader;
    private ZoomControl? _zoom;

    public LocalCameraSource(CameraDescriptor descriptor)
    {
        _groupId = descriptor.Id;
        Id = descriptor.Id;
        Name = descriptor.Name;
    }

    public string Id { get; }
    public string Name { get; }
    public string Kind => "local";
    public int Width { get; private set; }
    public int Height { get; private set; }
    public double Fps { get; private set; }
    public bool IsRunning => _reader is not null;
    public FrameRing Ring { get; } = new();
    public event Action<ICameraSource, VideoFrame>? FrameArrived;

    public bool SupportsHardwareZoom => _zoom?.Supported == true && _zoom.Max > _zoom.Min;
    public float MinZoom => SupportsHardwareZoom ? _zoom!.Min : 1f;
    public float MaxZoom => SupportsHardwareZoom ? _zoom!.Max : 1f;

    public static async Task<IReadOnlyList<CameraDescriptor>> EnumerateAsync()
    {
        var groups = await MediaFrameSourceGroup.FindAllAsync();
        return groups
            .Where(g => g.SourceInfos.Any(i => i.SourceKind == MediaFrameSourceKind.Color))
            .Select(g => new CameraDescriptor(g.Id, g.DisplayName, "local"))
            .ToList();
    }

    public async Task StartAsync(CaptureFormatPreference pref)
    {
        if (IsRunning) return;
        var group = await MediaFrameSourceGroup.FromIdAsync(_groupId)
            ?? throw new InvalidOperationException($"Camera '{Name}' is no longer connected.");

        _capture = new MediaCapture();
        await _capture.InitializeAsync(new MediaCaptureInitializationSettings
        {
            SourceGroup = group,
            SharingMode = MediaCaptureSharingMode.ExclusiveControl,
            StreamingCaptureMode = StreamingCaptureMode.Video,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu,
        });

        // Prefer the record stream (full quality) over preview-only pins.
        var info = group.SourceInfos
            .Where(i => i.SourceKind == MediaFrameSourceKind.Color)
            .OrderBy(i => i.MediaStreamType switch
            {
                MediaStreamType.VideoRecord => 0,
                MediaStreamType.VideoPreview => 1,
                _ => 2,
            })
            .First();
        _source = _capture.FrameSources[info.Id];

        var format = PickFormat(_source.SupportedFormats, pref);
        if (format is not null) await _source.SetFormatAsync(format);
        var current = _source.CurrentFormat;
        Width = (int)current.VideoFormat.Width & ~1;
        Height = (int)current.VideoFormat.Height & ~1;
        Fps = current.FrameRate.Denominator == 0 ? 30 : (double)current.FrameRate.Numerator / current.FrameRate.Denominator;

        _zoom = _capture.VideoDeviceController.ZoomControl;

        _reader = await _capture.CreateFrameReaderAsync(_source, MediaEncodingSubtypes.Nv12);
        _reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
        _reader.FrameArrived += OnFrameArrived;
        var status = await _reader.StartAsync();
        if (status != MediaFrameReaderStartStatus.Success)
        {
            await StopAsync();
            throw new InvalidOperationException($"Camera '{Name}' failed to start: {status}.");
        }
    }

    /// <summary>Highest frame rate first (slow motion matters most), then the largest size that fits.</summary>
    private static MediaFrameFormat? PickFormat(IReadOnlyList<MediaFrameFormat> formats, CaptureFormatPreference pref)
    {
        static double FpsOf(MediaFrameFormat f) =>
            f.FrameRate.Denominator == 0 ? 0 : (double)f.FrameRate.Numerator / f.FrameRate.Denominator;

        var fitting = formats
            .Where(f => f.VideoFormat.Width <= pref.MaxWidth && f.VideoFormat.Height <= pref.MaxHeight)
            .ToList();
        if (fitting.Count == 0) return null;

        // Among formats at >= 720p, take the fastest; ties go to the larger size.
        var candidates = fitting.Where(f => f.VideoFormat.Height >= Math.Min(720, pref.MaxHeight)).ToList();
        if (candidates.Count == 0) candidates = fitting;
        return candidates
            .OrderByDescending(f => Math.Round(FpsOf(f)))
            .ThenByDescending(f => f.VideoFormat.Width * f.VideoFormat.Height)
            // Uncompressed formats avoid a CPU MJPEG decode at high frame rates.
            .ThenBy(f => f.Subtype.Equals("MJPG", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .FirstOrDefault(f => FpsOf(f) >= pref.MinFps) ?? candidates.First();
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        using var frameRef = sender.TryAcquireLatestFrame();
        var bitmap = frameRef?.VideoMediaFrame?.SoftwareBitmap;
        if (frameRef is null || bitmap is null) return;

        long ts = frameRef.SystemRelativeTime is TimeSpan srt ? Clock.FromSystemRelative(srt) : Clock.NowUs();
        SoftwareBitmap? converted = null;
        try
        {
            if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Nv12)
                bitmap = converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Nv12);

            int w = bitmap.PixelWidth & ~1, h = bitmap.PixelHeight & ~1;
            var frame = _pool.Rent(w, h, ts);
            Nv12.CopyPacked(bitmap, frame.Data, w, h);
            Ring.Add(frame);
            FrameArrived?.Invoke(this, frame);
        }
        finally
        {
            converted?.Dispose();
            bitmap.Dispose();
        }
    }

    public MediaSource? CreatePreviewSource() => _source is null ? null : MediaSource.CreateFromMediaFrameSource(_source);

    public async Task SetHardwareZoomAsync(float zoom)
    {
        if (!SupportsHardwareZoom) return;
        float z = Math.Clamp(zoom, _zoom!.Min, _zoom.Max);
        _zoom.Configure(new ZoomSettings { Mode = ZoomTransitionMode.Smooth, Value = z });
        await Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_reader is not null)
        {
            _reader.FrameArrived -= OnFrameArrived;
            await _reader.StopAsync();
            _reader.Dispose();
            _reader = null;
        }
        _capture?.Dispose();
        _capture = null;
        _source = null;
        _zoom = null;
        Ring.Clear();
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}

[ComImport]
[Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe interface IMemoryBufferByteAccess
{
    void GetBuffer(out byte* buffer, out uint capacity);
}

internal static class Nv12
{
    /// <summary>Copies an NV12 SoftwareBitmap into a tightly packed buffer (row padding removed).</summary>
    public static unsafe void CopyPacked(SoftwareBitmap bitmap, byte[] dest, int width, int height)
    {
        using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
        using var reference = buffer.CreateReference();
        reference.As<IMemoryBufferByteAccess>().GetBuffer(out byte* src, out _);

        var y = buffer.GetPlaneDescription(0);
        var uv = buffer.GetPlaneDescription(1);
        fixed (byte* dst = dest)
        {
            for (int row = 0; row < height; row++)
                Buffer.MemoryCopy(src + y.StartIndex + row * y.Stride, dst + row * width, width, width);
            byte* dstUv = dst + width * height;
            for (int row = 0; row < height / 2; row++)
                Buffer.MemoryCopy(src + uv.StartIndex + row * uv.Stride, dstUv + row * width, width, width);
        }
    }

    /// <summary>A packed NV12 frame as a SoftwareBitmap (for thumbnails and stills).</summary>
    public static SoftwareBitmap ToSoftwareBitmap(VideoFrame f)
    {
        var nv12 = SoftwareBitmap.CreateCopyFromBuffer(
            System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(f.Data, 0, f.Length),
            BitmapPixelFormat.Nv12, f.Width, f.Height);
        var bgra = SoftwareBitmap.Convert(nv12, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        nv12.Dispose();
        return bgra;
    }
}
