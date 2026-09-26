using System.Text.Json;
using System.Threading.Channels;
using Microsoft.UI.Dispatching;
using SwingLoop.Capture;
using SwingLoop.Interop;
using SwingLoop.Models;
using SwingLoop.Services;

namespace SwingLoop.Analysis;

public sealed record ShotSavedArgs(SessionStore Store, ShotDto Shot);

/// <summary>
/// Cameras + microphone -> swing detector -> multi-camera clips on disk.
///
/// Threads: each camera delivers frames on its own capture thread; the
/// detector camera's frames go through a 1-slot channel to the analysis
/// thread (stale frames are dropped, never queued); audio arrives on the
/// AudioGraph thread; detector events are drained on the UI thread.
/// </summary>
public sealed class SwingPipeline : IAsyncDisposable
{
    private readonly SettingsService _settings;
    private readonly DispatcherQueue _ui;
    private readonly SwingDetector _detector = new();
    private readonly List<ICameraSource> _cameras = [];
    private readonly AudioCapture _audio = new();
    private readonly Channel<VideoFrame> _analysisQueue;
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherQueueTimer _pollTimer;
    private PoseEstimator? _pose;
    private Task? _analysisTask;
    private SwingPhase _lastPhase = SwingPhase.NoGolfer;
    private bool _lastPresent;
    private long _lastPoseUs;

    public SwingPipeline(SettingsService settings, DispatcherQueue ui)
    {
        _settings = settings;
        _ui = ui;
        _analysisQueue = Channel.CreateBounded<VideoFrame>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            dropped => dropped.Release());
        _audio.SamplesArrived += (ts, samples, rate) => _detector.PushAudio(ts, samples.Span, rate);
        _pollTimer = ui.CreateTimer();
        _pollTimer.Interval = TimeSpan.FromMilliseconds(30);
        _pollTimer.Tick += (_, _) => Drain();
    }

    public event Action<SwingPhase>? PhaseChanged;
    public event Action<bool>? GolferPresenceChanged;
    public event Action<ShotSavedArgs>? ShotSaved;
    public event Action<string>? Notice;
    /// <summary>A full swing with no ball struck. Waggles are ignored silently.</summary>
    public event Action<string>? SwingIgnored;

    public IReadOnlyList<ICameraSource> Cameras => _cameras;
    public SessionStore? Session { get; set; }
    public string PoseStatus { get; private set; } = "";
    public string AudioStatus { get; private set; } = "";
    public double PoseHz { get; set; } = 30;
    public bool HasPose => _pose is not null;
    public bool HasAudio => _audio.IsRunning;
    public (float LevelDb, float FloorDb) AudioLevels => _detector.AudioLevels;

    // Read from capture/analysis threads, so cached on the UI thread in ApplyDetectorConfig
    // rather than computed from the (UI-owned) camera list.
    private volatile ICameraSource? _detectorCamera;
    private volatile int _detectorRotation;

    public ICameraSource? DetectorCamera => _detectorCamera;

    public async Task StartAsync()
    {
        _pose = await Task.Run(() =>
        {
            var pose = PoseEstimator.TryCreate(PoseEstimator.DefaultModelPath, out var status);
            PoseStatus = status;
            return pose;
        });
        try
        {
            await _audio.StartAsync(_settings.Current.MicrophoneId);
            AudioStatus = "Listening for impact.";
        }
        catch (Exception ex)
        {
            AudioStatus = ex.Message;
        }
        ApplyDetectorConfig();
        _analysisTask = Task.Run(AnalysisLoopAsync);
        _pollTimer.Start();
    }

    public async Task AddCameraAsync(ICameraSource camera)
    {
        var s = _settings.Current;
        camera.Ring.MaxBytes = (long)s.BufferMemoryMbPerCamera * 1024 * 1024;
        camera.Ring.MaxDurationUs = (long)((s.PreRollSeconds + s.PostRollSeconds + 3.5) * 1_000_000);
        camera.FrameArrived += OnFrame;
        await camera.StartAsync(new CaptureFormatPreference(MaxWidth: s.MaxCaptureHeight * 16 / 9, MaxHeight: s.MaxCaptureHeight));
        _cameras.Add(camera);
        ApplyDetectorConfig();
    }

    public async Task RemoveCameraAsync(ICameraSource camera)
    {
        camera.FrameArrived -= OnFrame;
        _cameras.Remove(camera);
        await camera.DisposeAsync();
        ApplyDetectorConfig();
    }

    public void ApplyDetectorConfig()
    {
        var s = _settings.Current;
        var cam = _cameras.FirstOrDefault(c => s.Cameras.Any(x => x.Id == c.Id && x.IsDetector)) ?? _cameras.FirstOrDefault();
        int rotation = cam is null ? 0 : s.CameraFor(cam.Id, cam.Name).Rotation;
        _detectorCamera = cam;
        _detectorRotation = rotation;
        float aspect = cam is null || cam.Height == 0 ? 16f / 9f : (float)cam.Width / cam.Height;
        if (rotation % 180 != 0) aspect = 1 / aspect;

        var config = new Dictionary<string, object>
        {
            ["usePose"] = s.UsePose && _pose is not null,
            ["useAudio"] = s.UseAudio && _audio.IsRunning,
            ["useMotion"] = s.UseMotion,
            ["requireImpactSound"] = s.RequireImpactSound,
            ["frameAspect"] = aspect,
            ["preRollUs"] = (long)(s.PreRollSeconds * 1_000_000),
            ["postRollUs"] = (long)(s.PostRollSeconds * 1_000_000),
        };
        _detector.Configure(JsonSerializer.Serialize(config));
        foreach (var c in _cameras)
            c.Ring.MaxDurationUs = (long)((s.PreRollSeconds + s.PostRollSeconds + 3.5) * 1_000_000);
    }

    /// <summary>"Save that one": keep the last few seconds as a shot.</summary>
    public void TriggerManual() => _detector.TriggerManual(Clock.NowUs());

    private void OnFrame(ICameraSource camera, VideoFrame frame)
    {
        if (camera != DetectorCamera) return;
        if (!_analysisQueue.Writer.TryWrite(frame.Retain())) frame.Release();
    }

    private async Task AnalysisLoopAsync()
    {
        var ct = _cts.Token;
        try
        {
            await foreach (var frame in _analysisQueue.Reader.ReadAllAsync(ct))
            {
                try
                {
                    _detector.PushLuma(frame.TimestampUs, frame.Data, frame.Width, frame.Height, frame.Width);
                    var pose = _pose;
                    if (pose is not null && _settings.Current.UsePose &&
                        frame.TimestampUs - _lastPoseUs >= (long)(1_000_000 / PoseHz))
                    {
                        _lastPoseUs = frame.TimestampUs;
                        _detector.PushPose(frame.TimestampUs, pose.Estimate(frame, _detectorRotation));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _ui.TryEnqueue(() => Notice?.Invoke($"Analysis error: {ex.Message}"));
                }
                finally
                {
                    frame.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Drain()
    {
        _detector.Tick(Clock.NowUs());
        while (_detector.Poll(out var e))
        {
            switch (e.Type)
            {
                case SwingEventType.SwingCaptured:
                    _ = SaveShotAsync(e, practice: false);
                    break;
                case SwingEventType.PracticeSwing:
                    if (_settings.Current.SavePracticeSwings) _ = SaveShotAsync(WithClipWindow(e), practice: true);
                    else SwingIgnored?.Invoke("Practice swing: no ball struck, not saved.");
                    break;
            }
        }

        var phase = _detector.Phase;
        if (phase != _lastPhase)
        {
            _lastPhase = phase;
            PhaseChanged?.Invoke(phase);
        }
        bool present = _detector.GolferPresent;
        if (present != _lastPresent)
        {
            _lastPresent = present;
            GolferPresenceChanged?.Invoke(present);
        }
    }

    private SwingEvent WithClipWindow(SwingEvent e)
    {
        var s = _settings.Current;
        e.ClipStartUs = Math.Min(e.ImpactUs - (long)(s.PreRollSeconds * 1e6), e.TakeawayUs > 0 ? e.TakeawayUs - 300_000 : long.MaxValue);
        e.ClipEndUs = Math.Max(e.ImpactUs + (long)(s.PostRollSeconds * 1e6), e.FinishUs + 300_000);
        return e;
    }

    private async Task SaveShotAsync(SwingEvent e, bool practice)
    {
        var store = Session;
        if (store is null) return;
        var cameras = _cameras.Where(c => c.IsRunning).ToList();
        if (cameras.Count == 0) return;

        // The detector fires around impact; wait for the follow-through to be recorded.
        long deadline = Math.Max(Clock.NowUs(), e.ClipEndUs) + 1_500_000;
        while (cameras.Any(c => c.Ring.NewestTimestampUs < e.ClipEndUs) && Clock.NowUs() < deadline)
            await Task.Delay(40);

        var detectorCam = DetectorCamera;
        var snapshots = cameras
            .OrderBy(c => c == detectorCam ? 0 : 1)
            .Select(c => (Camera: c, Frames: c.Ring.Snapshot(e.ClipStartUs, e.ClipEndUs)))
            .ToList();
        ShotDto? shot = null;
        try
        {
            shot = new ShotDto
            {
                Timing = new TimingDto
                {
                    AddressUs = e.AddressUs, TakeawayUs = e.TakeawayUs, TopUs = e.TopUs,
                    ImpactUs = e.ImpactUs, FinishUs = e.FinishUs, TempoRatio = e.TempoRatio,
                },
                Confidence = e.Confidence,
                ImpactConfirmed = e.ImpactConfirmed != 0,
                Source = e.Source switch { CaptureSource.AudioMotion => "audio", CaptureSource.Manual => "manual", _ => "pose" },
                Tags = practice ? ["practice"] : [],
            };
            shot = store.AddShot(shot);
            string folder = store.ShotFolder(shot);

            var clips = await Task.WhenAll(snapshots.Where(s => s.Frames.Count >= 2).Select(async (s, index) =>
            {
                var camSettings = _settings.Current.CameraFor(s.Camera.Id, s.Camera.Name);
                string baseName = $"cam{index + 1}-{Slug(camSettings.Position)}";
                string mp4 = Path.Combine(folder, baseName + ".mp4");
                string jpg = Path.Combine(folder, baseName + ".jpg");
                var result = await Task.Run(() => ClipWriter.WriteMp4Async(s.Frames, mp4, e.ImpactUs));
                await ClipWriter.WriteThumbnailAsync(s.Frames, e.ImpactUs, jpg);
                return new ClipDto
                {
                    CameraId = s.Camera.Id,
                    File = baseName + ".mp4",
                    Thumbnail = baseName + ".jpg",
                    Width = result.Width,
                    Height = result.Height,
                    Fps = result.Fps,
                    DurationUs = result.DurationUs,
                    ImpactOffsetUs = result.ImpactOffsetUs,
                    RotationDeg = camSettings.Rotation,
                };
            }));

            shot.Clips = clips.ToList();
            store.UpdateShot(shot);
            _ui.TryEnqueue(() => ShotSaved?.Invoke(new ShotSavedArgs(store, shot)));
        }
        catch (Exception ex)
        {
            // Don't leave a shot with no video behind.
            if (shot is not null && !string.IsNullOrEmpty(shot.Id) && shot.Clips.Count == 0)
            {
                try { store.RemoveShot(shot); } catch (Exception) { }
            }
            _ui.TryEnqueue(() => Notice?.Invoke($"Could not save shot: {ex.Message}"));
        }
        finally
        {
            foreach (var s in snapshots)
                foreach (var f in s.Frames) f.Release();
        }
    }

    private static string Slug(string s) =>
        new string(s.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');

    public async ValueTask DisposeAsync()
    {
        _pollTimer.Stop();
        _cts.Cancel();
        _analysisQueue.Writer.TryComplete();
        if (_analysisTask is not null)
        {
            try { await _analysisTask; } catch (OperationCanceledException) { }
        }
        foreach (var c in _cameras.ToList()) await RemoveCameraAsync(c);
        _audio.Dispose();
        _pose?.Dispose();
        _detector.Dispose();
        _cts.Dispose();
    }
}
