using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SwingLoop.Capture;

public sealed record ClipResult(int Width, int Height, double Fps, long DurationUs, long ImpactOffsetUs);

/// <summary>Encodes ring-buffer frames to H.264 MP4 with the hardware encoder.</summary>
public static class ClipWriter
{
    public static async Task<ClipResult> WriteMp4Async(IReadOnlyList<VideoFrame> frames, string path, long impactUs,
        CancellationToken ct = default)
    {
        if (frames.Count < 2) throw new ArgumentException("need at least two frames", nameof(frames));
        var first = frames[0];
        int w = first.Width, h = first.Height;
        double fps = EstimateFps(frames);
        long frameUs = (long)(1_000_000 / fps);

        var props = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Nv12, (uint)w, (uint)h);
        props.FrameRate.Numerator = (uint)Math.Round(fps * 1000);
        props.FrameRate.Denominator = 1000;
        var descriptor = new VideoStreamDescriptor(props);

        long durationUs = frames[^1].TimestampUs - first.TimestampUs + frameUs;
        var mss = new MediaStreamSource(descriptor)
        {
            BufferTime = TimeSpan.Zero,
            Duration = TimeSpan.FromTicks(durationUs * 10),
            CanSeek = false,
        };

        int index = 0;
        mss.Starting += (_, e) => e.Request.SetActualStartPosition(TimeSpan.Zero);
        mss.SampleRequested += (_, e) =>
        {
            if (index >= frames.Count || ct.IsCancellationRequested)
            {
                e.Request.Sample = null;  // end of stream
                return;
            }
            var f = frames[index];
            long rel = f.TimestampUs - first.TimestampUs;
            long next = index + 1 < frames.Count ? frames[index + 1].TimestampUs - first.TimestampUs : rel + frameUs;
            var sample = MediaStreamSample.CreateFromBuffer(f.Data.AsBuffer(0, f.Length), TimeSpan.FromTicks(rel * 10));
            sample.Duration = TimeSpan.FromTicks(Math.Max(1, next - rel) * 10);
            sample.KeyFrame = true;
            e.Request.Sample = sample;
            index++;
        };

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Audio = null;
        profile.Video.Width = (uint)w;
        profile.Video.Height = (uint)h;
        profile.Video.FrameRate.Numerator = props.FrameRate.Numerator;
        profile.Video.FrameRate.Denominator = props.FrameRate.Denominator;
        // ~0.1 bits per pixel per frame keeps fast club motion crisp.
        profile.Video.Bitrate = (uint)Math.Clamp(w * h * fps * 0.1, 4_000_000, 60_000_000);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (File.Create(path)) { }
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);

        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
        var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(mss, stream, profile);
        if (!prepared.CanTranscode)
            throw new InvalidOperationException($"Cannot encode clip: {prepared.FailureReason}");
        await prepared.TranscodeAsync().AsTask(ct);

        return new ClipResult(w, h, fps, durationUs, Math.Max(0, impactUs - first.TimestampUs));
    }

    /// <summary>Saves the frame nearest <paramref name="atUs"/> as a small JPEG.</summary>
    public static async Task WriteThumbnailAsync(IReadOnlyList<VideoFrame> frames, long atUs, string path, int width = 480)
    {
        var frame = frames.MinBy(f => Math.Abs(f.TimestampUs - atUs))!;
        using var bitmap = Nv12.ToSoftwareBitmap(frame);
        using var ignoreAlpha = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);

        using (File.Create(path)) { }
        var file = await StorageFile.GetFileFromPathAsync(path);
        using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
        encoder.SetSoftwareBitmap(ignoreAlpha);
        encoder.BitmapTransform.ScaledWidth = (uint)width;
        encoder.BitmapTransform.ScaledHeight = (uint)(width * (double)frame.Height / frame.Width);
        encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        await encoder.FlushAsync();
    }

    private static double EstimateFps(IReadOnlyList<VideoFrame> frames)
    {
        var deltas = new List<long>(frames.Count);
        for (int i = 1; i < frames.Count; i++) deltas.Add(frames[i].TimestampUs - frames[i - 1].TimestampUs);
        deltas.Sort();
        long median = deltas[deltas.Count / 2];
        return median > 0 ? Math.Clamp(1_000_000.0 / median, 5, 480) : 30;
    }
}
