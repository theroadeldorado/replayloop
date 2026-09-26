using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using SwingLoop.Models;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace SwingLoop.Analysis;

/// <summary>One clip being replayed (a camera angle of a shot).</summary>
public sealed class ReplayTrack
{
    public required string Path { get; init; }
    public required ClipDto Clip { get; init; }
    public required ShotDto Shot { get; init; }
    public required TimeSpan Offset { get; init; }  // clip position = timeline position + Offset
    public string Label { get; init; } = "";
    internal List<MediaPlayer> Players { get; } = [];
}

/// <summary>
/// Plays any number of clips in lockstep on one MediaTimelineController, all
/// aligned at impact: multi-angle replay, shot-vs-shot compare, and the PiP
/// window all follow the same clock, speed and loop.
/// </summary>
public sealed partial class ReplayController : ObservableObject, IDisposable
{
    private readonly MediaTimelineController _timeline = new();
    private readonly DispatcherQueueTimer _timer;
    private readonly List<ReplayTrack> _tracks = [];
    private bool _scrubbing;

    public ReplayController(DispatcherQueue ui)
    {
        _timer = ui.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(15);
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
    }

    public IReadOnlyList<ReplayTrack> Tracks => _tracks;

    [ObservableProperty] private TimeSpan _duration;
    [ObservableProperty] private TimeSpan _position;
    [ObservableProperty] private TimeSpan _impactPosition;
    [ObservableProperty] private double _rate = 1.0;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _loop = true;
    [ObservableProperty] private double _frameRate = 60;

    /// <summary>Pause between loops so the eye can reset.</summary>
    public TimeSpan LoopGap { get; set; } = TimeSpan.FromMilliseconds(350);

    public event Action? TracksChanged;

    public double PositionSeconds
    {
        get => Position.TotalSeconds;
        set => Seek(TimeSpan.FromSeconds(value));
    }

    public double DurationSeconds => Math.Max(0.01, Duration.TotalSeconds);

    /// <summary>Replace what is playing. Clips are aligned so their impacts coincide.</summary>
    public void Load(IEnumerable<(string Path, ClipDto Clip, ShotDto Shot, string Label)> clips, bool autoplay = true)
    {
        Clear();
        var list = clips.Where(c => File.Exists(c.Path)).ToList();
        if (list.Count == 0)
        {
            TracksChanged?.Invoke();
            return;
        }

        // Timeline 0 = start of the clip whose impact comes earliest.
        long anchor = list.Min(c => c.Clip.ImpactOffsetUs);
        long end = 0;
        foreach (var c in list)
        {
            long offsetUs = c.Clip.ImpactOffsetUs - anchor;
            end = Math.Max(end, c.Clip.DurationUs - offsetUs);
            _tracks.Add(new ReplayTrack
            {
                Path = c.Path, Clip = c.Clip, Shot = c.Shot, Label = c.Label,
                Offset = TimeSpan.FromTicks(offsetUs * 10),
            });
        }
        Duration = TimeSpan.FromTicks(end * 10);
        ImpactPosition = TimeSpan.FromTicks(anchor * 10);
        FrameRate = list.Max(c => c.Clip.Fps > 0 ? c.Clip.Fps : 30);
        OnPropertyChanged(nameof(DurationSeconds));

        _timeline.Position = TimeSpan.Zero;
        _timeline.ClockRate = Rate;
        TracksChanged?.Invoke();
        if (autoplay) Play();
    }

    /// <summary>A player for <paramref name="track"/> slaved to the shared timeline. Owned by the controller.</summary>
    public MediaPlayer CreatePlayer(ReplayTrack track)
    {
        var player = new MediaPlayer
        {
            Source = MediaSource.CreateFromUri(new Uri(track.Path)),
            IsMuted = true,
            AutoPlay = false,
        };
        player.CommandManager.IsEnabled = false;
        player.TimelineController = _timeline;
        player.TimelineControllerPositionOffset = track.Offset;
        track.Players.Add(player);
        return player;
    }

    public void ReleasePlayer(MediaPlayer player)
    {
        foreach (var t in _tracks) t.Players.Remove(player);
        player.TimelineController = null;
        player.Dispose();
    }

    public void Play()
    {
        if (_tracks.Count == 0) return;
        if (_timeline.State == MediaTimelineControllerState.Paused) _timeline.Resume();
        else _timeline.Start();
        IsPlaying = true;
    }

    public void Pause()
    {
        _timeline.Pause();
        IsPlaying = false;
    }

    public void Toggle()
    {
        if (IsPlaying) Pause();
        else Play();
    }

    public void Seek(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t > Duration) t = Duration;
        _timeline.Position = t;
        Position = t;
    }

    public void BeginScrub()
    {
        _scrubbing = true;
        _timeline.Pause();
    }

    public void EndScrub()
    {
        _scrubbing = false;
        if (IsPlaying) _timeline.Resume();
    }

    public void StepFrames(int frames)
    {
        Pause();
        Seek(_timeline.Position + TimeSpan.FromSeconds(frames / FrameRate));
    }

    public void JumpToImpact()
    {
        Pause();
        Seek(ImpactPosition);
    }

    partial void OnRateChanged(double value) => _timeline.ClockRate = value;

    private DateTime _loopPauseUntil;

    private void OnTick()
    {
        if (_tracks.Count == 0 || _scrubbing) return;
        var pos = _timeline.Position;
        if (IsPlaying && pos >= Duration)
        {
            if (!Loop)
            {
                Pause();
                pos = Duration;
            }
            else if (_loopPauseUntil == default)
            {
                _timeline.Pause();
                _loopPauseUntil = DateTime.UtcNow + LoopGap;
            }
            else if (DateTime.UtcNow >= _loopPauseUntil)
            {
                _loopPauseUntil = default;
                _timeline.Position = TimeSpan.Zero;
                _timeline.Resume();
                pos = TimeSpan.Zero;
            }
        }
        if (pos != Position)
        {
            Position = pos;
            OnPropertyChanged(nameof(PositionSeconds));
        }
    }

    public void Clear()
    {
        _timeline.Pause();
        foreach (var t in _tracks)
            foreach (var p in t.Players.ToList())
            {
                p.TimelineController = null;
                p.Dispose();
            }
        _tracks.Clear();
        Duration = TimeSpan.Zero;
        Position = TimeSpan.Zero;
        IsPlaying = false;
        _loopPauseUntil = default;
    }

    public void Dispose()
    {
        _timer.Stop();
        Clear();
    }
}
