using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SwingLoop.Analysis;
using SwingLoop.Capture;
using SwingLoop.Controls;
using SwingLoop.Interop;
using SwingLoop.Models;
using SwingLoop.Services;
using SwingLoop.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Devices.Enumeration;
using Windows.Graphics;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;

namespace SwingLoop;

public sealed partial class MainWindow : Window
{
    private static readonly string[] Clubs =
        ["Driver", "3 Wood", "5 Wood", "Hybrid", "3 Iron", "4 Iron", "5 Iron", "6 Iron", "7 Iron", "8 Iron", "9 Iron", "PW", "GW", "SW", "LW", "Putter"];

    private readonly SettingsService _settings = new();
    private readonly SwingPipeline _pipeline;
    private readonly ReplayController _replay;
    private readonly CastService _cast = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _uiTimer;

    private SessionStore? _liveStore;  // receives new captures
    private SessionStore? _viewStore;  // shown in the strip: the live session or a past one

    private readonly Dictionary<string, (VideoSurface Surface, MediaPlayer Player)> _live = new();
    private readonly List<VideoSurface> _replaySurfaces = [];
    private readonly Dictionary<AnnotationCanvas, (SessionStore Store, ShotDto? Shot)> _canvasTargets = new();
    private VideoSurface? _inset;
    private MediaPlayer? _insetPlayer;
    private AnnotationCanvas? _lastEditedCanvas;

    private PipWindow? _pip;
    private readonly List<(VideoSurface Surface, MediaPlayer Player, bool OwnedByUs)> _pipSurfaces = [];

    private MediaPlayer? _castPlayer;
    private bool _updatingUi;
    private bool _started;

    public MainViewModel ViewModel { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new SizeInt32(1600, 980));

        _settings.Load();
        _pipeline = new SwingPipeline(_settings, DispatcherQueue);
        _pipeline.PhaseChanged += p => ViewModel.Phase = p;
        _pipeline.GolferPresenceChanged += present => ViewModel.GolferInView = present;
        _pipeline.ShotSaved += OnShotSaved;
        _pipeline.Notice += msg => ShowToast(msg, InfoBarSeverity.Warning);
        _pipeline.SwingIgnored += reason => ShowToast(reason, InfoBarSeverity.Informational, 2);

        _replay = new ReplayController(DispatcherQueue) { Rate = _settings.Current.DefaultSpeed };
        _replay.TracksChanged += RebuildReplaySurfaces;
        _replay.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ReplayController.Position) or nameof(ReplayController.Duration)) UpdateScrubber();
            if (e.PropertyName == nameof(ReplayController.IsPlaying))
                PlayGlyph.Glyph = _replay.IsPlaying ? "" : "";
        };

        _uiTimer = DispatcherQueue.CreateTimer();
        _uiTimer.Interval = TimeSpan.FromMilliseconds(100);
        _uiTimer.Tick += (_, _) => UpdateMeters();
        _uiTimer.Start();

        Root.PreviewKeyDown += Root_PreviewKeyDown;
        Scrubber.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => _replay.BeginScrub()), true);
        Scrubber.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => _replay.EndScrub()), true);
        Scrubber.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => _replay.EndScrub()), true);
        Scrubber.SizeChanged += (_, _) => PositionImpactMarker();
        ClubBox.ItemsSource = Clubs;

        BuildSwatches();
        LoadSettingsIntoUi();
        Activated += async (_, _) =>
        {
            if (_started) return;
            _started = true;
            await StartAsync();
        };
        Closed += OnClosed;
    }

    // ================================================================= startup / shutdown

    private async Task StartAsync()
    {
        try
        {
            _liveStore = SessionStore.Create(_settings.Current.SessionsRoot);
            _viewStore = _liveStore;
            _pipeline.Session = _liveStore;
            ViewModel.SessionName = _liveStore.Name;
        }
        catch (Exception ex)
        {
            ShowToast($"Cannot create a session in {_settings.Current.SessionsRoot}: {ex.Message}", InfoBarSeverity.Error, 0);
        }

        await _pipeline.StartAsync();
        PoseStatusText.Text = _pipeline.PoseStatus;
        AudioStatusText.Text = _pipeline.AudioStatus;
        await RescanCamerasAsync();
        RefreshPastSessions();
        UpdateStage();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        // The session ends when the app closes.
        try { _liveStore?.End(); } catch (Exception) { }
        _settings.SaveNow();
        _pip?.Close();
        _ = _cast.StopAsync();
        _castPlayer?.Dispose();
        _replay.Dispose();
        _ = _pipeline.DisposeAsync();
    }

    // ================================================================= cameras

    private async Task RescanCamerasAsync()
    {
        IReadOnlyList<CameraDescriptor> found;
        try
        {
            found = await LocalCameraSource.EnumerateAsync();
        }
        catch (Exception ex)
        {
            ShowToast($"Could not list cameras: {ex.Message}", InfoBarSeverity.Error);
            return;
        }

        foreach (var gone in ViewModel.Cameras.Where(c => found.All(f => f.Id != c.Id)).ToList())
        {
            await StopCameraAsync(gone);
            ViewModel.Cameras.Remove(gone);
        }
        foreach (var d in found)
        {
            if (ViewModel.Cameras.Any(c => c.Id == d.Id)) continue;
            var vm = new CameraViewModel(d, _settings.Current.CameraFor(d.Id, d.Name));
            ViewModel.Cameras.Add(vm);
            if (vm.Settings.Enabled) await StartCameraAsync(vm);
            else vm.Status = "Off";
        }
        EnsureDetectorCamera();
        _settings.Save();
        UpdateSessionCameras();
        RebuildLiveSurfaces();
        _pipeline.ApplyDetectorConfig();
        UpdateStage();
    }

    private void EnsureDetectorCamera()
    {
        var running = ViewModel.Cameras.Where(c => c.Source is not null).ToList();
        if (running.Count == 0 || running.Any(c => c.Settings.IsDetector)) return;
        foreach (var c in ViewModel.Cameras) c.Settings.IsDetector = false;
        running[0].Settings.IsDetector = true;
    }

    private async Task StartCameraAsync(CameraViewModel vm)
    {
        vm.Status = "Starting…";
        try
        {
            var source = new LocalCameraSource(vm.Descriptor);
            await _pipeline.AddCameraAsync(source);
            vm.Source = source;
            vm.Status = $"{source.Width}×{source.Height} at {source.Fps:0} fps · {(source.SupportsHardwareZoom ? "optical zoom" : "digital zoom")}";
            if (vm.Settings.Zoom > 1 && source.SupportsHardwareZoom) await source.SetHardwareZoomAsync(vm.Settings.Zoom);
        }
        catch (UnauthorizedAccessException)
        {
            vm.Status = "Camera access is blocked. Allow it in Settings > Privacy & security > Camera.";
        }
        catch (Exception ex)
        {
            vm.Status = ex.Message;
        }
    }

    private async Task StopCameraAsync(CameraViewModel vm)
    {
        if (vm.Source is null) return;
        var source = vm.Source;
        vm.Source = null;
        RemoveLiveSurface(vm.Id);
        await _pipeline.RemoveCameraAsync(source);
        vm.Status = "Off";
    }

    private void UpdateSessionCameras()
    {
        _liveStore?.SetCameras(ViewModel.Cameras.Where(c => c.Source is not null).Select(c => new CameraDto
        {
            Id = c.Id, Name = c.Name, Kind = "local", Position = c.Settings.Position,
        }).ToList());
    }

    private IEnumerable<CameraViewModel> LiveCameras()
    {
        var running = ViewModel.Cameras.Where(c => c.Source is not null && c.Settings.ShowLive).ToList();
        if (!_settings.Current.MultiLiveView)
            running = running.OrderByDescending(c => c.Settings.IsDetector).Take(1).ToList();
        return running;
    }

    // ================================================================= surfaces

    private VideoSurface NewSurface(string cameraId, int width, int height, int rotation, CameraSettings? cam)
    {
        var s = new VideoSurface
        {
            CameraId = cameraId,
            Fill = AppSettings.ParseAspect(_settings.Current.UniformAspect) > 0,
        };
        s.SetSource(width, height, rotation, cam?.FineRotation ?? 0, cam?.Mirror ?? false);
        s.Annotations.ShapesChanged += OnShapesChanged;
        ApplyDrawingState(s.Annotations);
        return s;
    }

    private void RebuildLiveSurfaces()
    {
        foreach (var id in _live.Keys.ToList()) RemoveLiveSurface(id);
        LiveTiles.UniformAspect = AppSettings.ParseAspect(_settings.Current.UniformAspect);

        foreach (var vm in LiveCameras())
        {
            var source = vm.Source!;
            var player = new MediaPlayer { RealTimePlayback = true, IsMuted = true, AutoPlay = true, Source = source.CreatePreviewSource() };
            var surface = NewSurface(vm.Id, source.Width, source.Height, vm.Settings.Rotation, vm.Settings);
            surface.Player = player;
            surface.Label = LiveCameras().Count() > 1 ? $"LIVE · {vm.Settings.Position}" : "LIVE";
            if (!source.SupportsHardwareZoom) surface.SetZoom(vm.Settings.Zoom);
            surface.ZoomRequested += async (_, z) => await OnLiveZoomAsync(vm, surface, z);
            // Drawings on the live view are alignment guides: kept for every shot.
            surface.Annotations.DrawPersistent = true;
            if (_liveStore is not null)
            {
                surface.Annotations.SetShapes([], _liveStore.Data.PersistentAnnotations);
                _canvasTargets[surface.Annotations] = (_liveStore, null);
            }
            LiveTiles.Children.Add(surface);
            _live[vm.Id] = (surface, player);
            player.Play();
        }
        RebuildInset();
        UpdateStage();
    }

    private void RemoveLiveSurface(string cameraId)
    {
        if (!_live.Remove(cameraId, out var entry)) return;
        LiveTiles.Children.Remove(entry.Surface);
        _canvasTargets.Remove(entry.Surface.Annotations);
        entry.Surface.Player = null;
        entry.Player.Dispose();
        if (_inset?.CameraId == cameraId) RebuildInset();
    }

    private async Task OnLiveZoomAsync(CameraViewModel vm, VideoSurface surface, float zoom)
    {
        // Prefer the camera's own (optical/sensor) zoom; the surface's digital zoom is the fallback.
        vm.Settings.Zoom = zoom;
        _settings.Save();
        if (vm.Source?.SupportsHardwareZoom == true)
        {
            surface.SetZoom(1);
            await vm.Source.SetHardwareZoomAsync(zoom);
        }
    }

    private void RebuildInset()
    {
        if (_inset is not null)
        {
            _inset.Player = null;
            _insetPlayer?.Dispose();
            _insetPlayer = null;
            LiveInsetHost.Child = null;
            _inset = null;
        }
        var cam = ViewModel.Cameras.Where(c => c.Source is not null).OrderByDescending(c => c.Settings.IsDetector).FirstOrDefault();
        if (cam is null || !_settings.Current.ShowLiveInset) return;

        _insetPlayer = new MediaPlayer { RealTimePlayback = true, IsMuted = true, AutoPlay = true, Source = cam.Source!.CreatePreviewSource() };
        _inset = NewSurface(cam.Id, cam.Source.Width, cam.Source.Height, cam.Settings.Rotation, cam.Settings);
        _inset.Player = _insetPlayer;
        _inset.Label = "LIVE";
        _inset.Annotations.Tool = DrawTool.None;
        LiveInsetHost.Child = _inset;
        float aspect = _inset.DisplayAspect;
        LiveInsetHost.Width = aspect >= 1 ? 320 : 180 * aspect;
        LiveInsetHost.Height = aspect >= 1 ? 320 / aspect : 180;
        LiveInsetHost.Tapped -= LiveInset_Tapped;
        LiveInsetHost.Tapped += LiveInset_Tapped;
        _insetPlayer.Play();
    }

    private void LiveInset_Tapped(object sender, TappedRoutedEventArgs e) => SetMode(StageMode.Live);

    private void RebuildReplaySurfaces()
    {
        // The controller has already disposed the previous players.
        foreach (var s in _replaySurfaces)
        {
            s.Player = null;
            _canvasTargets.Remove(s.Annotations);
        }
        _replaySurfaces.Clear();
        ReplayTiles.Children.Clear();
        ReplayTiles.UniformAspect = AppSettings.ParseAspect(_settings.Current.UniformAspect);
        ReplayTiles.Stack = ViewModel.Mode == StageMode.Compare && ViewModel.CompareOverlay;

        var store = _viewStore;
        for (int i = 0; i < _replay.Tracks.Count; i++)
        {
            var track = _replay.Tracks[i];
            var surface = CreateReplaySurface(track, store);
            if (ReplayTiles.Stack && i > 0) surface.Opacity = 0.5;
            ReplayTiles.Children.Add(surface);
            _replaySurfaces.Add(surface);
        }
        PositionImpactMarker();
        RebuildPip(replayPlayersDisposed: true);
        UpdateCastSource();
        UpdateStage();
    }

    private VideoSurface CreateReplaySurface(ReplayTrack track, SessionStore? store, bool interactive = true)
    {
        var cam = _settings.Current.Cameras.FirstOrDefault(c => c.Id == track.Clip.CameraId);
        int rotation = cam?.Rotation ?? track.Clip.RotationDeg;
        var surface = NewSurface(track.Clip.CameraId, track.Clip.Width, track.Clip.Height, rotation, cam);
        surface.Player = _replay.CreatePlayer(track);
        surface.Label = track.Label;
        if (store is not null)
        {
            surface.Annotations.SetShapes(track.Shot.Annotations, store.Data.PersistentAnnotations);
            if (interactive) _canvasTargets[surface.Annotations] = (store, track.Shot);
        }
        if (!interactive) surface.Annotations.Tool = DrawTool.None;
        return surface;
    }

    // ================================================================= shots & sessions

    private void OnShotSaved(ShotSavedArgs args)
    {
        if (args.Store != _viewStore)
        {
            ShowToast($"Shot #{args.Shot.Number} saved to the current session.", InfoBarSeverity.Success, 3);
            return;
        }
        var vm = new ShotViewModel(args.Store, args.Shot);
        ViewModel.Shots.Add(vm);
        ShotStrip.ScrollIntoView(vm);
        if (_settings.Current.AutoReplay && ViewModel.Mode != StageMode.Compare)
        {
            SelectShot(vm);
            SetMode(StageMode.Replay);
        }
        UpdateStage();
    }

    private void LoadShots(SessionStore store)
    {
        ViewModel.Shots.Clear();
        foreach (var s in store.Data.Shots) ViewModel.Shots.Add(new ShotViewModel(store, s));
        ViewModel.SessionName = store.Name;
        ViewModel.SelectedShot = null;
        ClearCompare();
        _replay.Clear();
        RebuildReplaySurfaces();
        var last = ViewModel.Shots.LastOrDefault();
        if (last is not null) SelectShot(last);
    }

    private void SelectShot(ShotViewModel vm)
    {
        _updatingUi = true;
        ShotStrip.SelectedItem = vm;
        _updatingUi = false;
        ViewModel.SelectedShot = vm;
        ShowShotDetails(vm);
        LoadReplay();
    }

    private void ShotStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi || e.AddedItems.Count == 0 || e.AddedItems[0] is not ShotViewModel picked) return;

        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        if (ctrl && ViewModel.SelectedShot is not null && picked != ViewModel.SelectedShot)
        {
            SetCompareTarget(picked);
            return;
        }
        ClearCompare();
        SelectShot(picked);
        if (ViewModel.Mode == StageMode.Live) SetMode(StageMode.Replay);
    }

    private void SetCompareTarget(ShotViewModel target)
    {
        ClearCompare();
        target.IsCompareTarget = true;
        ViewModel.CompareShot = target;
        _updatingUi = true;
        ShotStrip.SelectedItem = ViewModel.SelectedShot;
        _updatingUi = false;
        SetMode(StageMode.Compare);
    }

    private void ClearCompare()
    {
        if (ViewModel.CompareShot is not null) ViewModel.CompareShot.IsCompareTarget = false;
        ViewModel.CompareShot = null;
    }

    private void ShotStrip_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not ShotViewModel shot) return;
        var menu = new MenuFlyout();
        var compare = new MenuFlyoutItem { Text = "Compare with selected shot", IsEnabled = ViewModel.SelectedShot is not null && ViewModel.SelectedShot != shot };
        compare.Click += (_, _) => SetCompareTarget(shot);
        var star = new MenuFlyoutItem { Text = shot.IsStarred ? "Remove star" : "Star" };
        star.Click += (_, _) =>
        {
            shot.IsStarred = !shot.IsStarred;
            if (shot == ViewModel.SelectedShot) ShowShotDetails(shot);
        };
        var delete = new MenuFlyoutItem { Text = "Delete shot" };
        delete.Click += async (_, _) => await DeleteShotAsync(shot);
        menu.Items.Add(compare);
        menu.Items.Add(star);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(delete);
        menu.ShowAt((UIElement)sender, e.GetPosition((UIElement)sender));
    }

    private void LoadReplay()
    {
        var a = ViewModel.SelectedShot;
        var clips = new List<(string, ClipDto, ShotDto, string)>();
        if (a is not null && ViewModel.Mode == StageMode.Compare && ViewModel.CompareShot is ShotViewModel b)
        {
            // Same angle from both shots, impacts lined up.
            var camId = PreferredCompareCamera(a.Dto, b.Dto);
            var ca = a.Dto.Clips.FirstOrDefault(c => c.CameraId == camId) ?? a.Dto.Clips.FirstOrDefault();
            var cb = b.Dto.Clips.FirstOrDefault(c => c.CameraId == camId) ?? b.Dto.Clips.FirstOrDefault();
            if (ca is not null) clips.Add((a.ClipPath(ca), ca, a.Dto, $"#{a.Number}"));
            if (cb is not null) clips.Add((b.ClipPath(cb), cb, b.Dto, $"#{b.Number}"));
        }
        else if (a is not null)
        {
            var shown = a.Dto.Clips.Where(c =>
                _settings.Current.Cameras.FirstOrDefault(s => s.Id == c.CameraId)?.ShowReplay ?? true).ToList();
            if (shown.Count == 0) shown = a.Dto.Clips.Take(1).ToList();
            if (!_settings.Current.MultiReplayView) shown = shown.Take(1).ToList();
            bool many = shown.Count > 1;
            foreach (var c in shown)
                clips.Add((a.ClipPath(c), c, a.Dto, many ? $"#{a.Number} · {CameraLabel(c.CameraId)}" : $"#{a.Number}"));
        }
        _replay.Load(clips);
        SyncSpeedBar();
    }

    private string CameraLabel(string cameraId) =>
        _settings.Current.Cameras.FirstOrDefault(c => c.Id == cameraId)?.Position
        ?? _viewStore?.Data.Cameras.FirstOrDefault(c => c.Id == cameraId)?.Position
        ?? "camera";

    private string? PreferredCompareCamera(ShotDto a, ShotDto b)
    {
        var common = a.Clips.Select(c => c.CameraId).Intersect(b.Clips.Select(c => c.CameraId)).ToList();
        var detector = _settings.Current.Cameras.FirstOrDefault(c => c.IsDetector)?.Id;
        return common.Contains(detector ?? "") ? detector : common.FirstOrDefault();
    }

    private void ShowShotDetails(ShotViewModel? shot)
    {
        _updatingUi = true;
        StarToggle.IsChecked = shot?.IsStarred ?? false;
        StarGlyph.Glyph = shot?.IsStarred == true ? "" : "";
        CommentBox.Text = shot?.Comment ?? "";
        ClubBox.Text = shot?.Club ?? "";
        _updatingUi = false;
    }

    private async Task DeleteShotAsync(ShotViewModel shot)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = $"Delete shot #{shot.Number}?",
            Content = "The video files are removed from the session folder.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        bool wasSelected = shot == ViewModel.SelectedShot;
        if (shot == ViewModel.CompareShot) ClearCompare();
        if (wasSelected) _replay.Clear();
        shot.Store.RemoveShot(shot.Dto);
        ViewModel.Shots.Remove(shot);
        if (wasSelected)
        {
            ViewModel.SelectedShot = null;
            if (ViewModel.Shots.LastOrDefault() is ShotViewModel last) SelectShot(last);
        }
        UpdateStage();
    }

    private void RefreshPastSessions()
    {
        ViewModel.PastSessions.Clear();
        foreach (var s in SessionStore.List(_settings.Current.SessionsRoot))
            if (s.Folder != _liveStore?.Folder || s.ShotCount > 0) ViewModel.PastSessions.Add(s);
    }

    private void Sessions_Click(object sender, RoutedEventArgs e) => RefreshPastSessions();

    private void SessionsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SessionSummaryDto summary) return;
        SessionsFlyout.Hide();
        if (string.Equals(Path.GetFullPath(summary.Folder), Path.GetFullPath(_liveStore?.Folder ?? ""), StringComparison.OrdinalIgnoreCase))
        {
            BackToLive_Click(this, new RoutedEventArgs());
            return;
        }
        try
        {
            var past = SessionStore.Open(summary.Folder);
            if (_viewStore != _liveStore) _viewStore?.Dispose();
            _viewStore = past;
            ViewModel.IsReviewingPast = true;
            LoadShots(past);
            SetMode(StageMode.Replay);
            ShowToast($"Reviewing “{past.Name}”. New swings still save to the current session.", InfoBarSeverity.Informational, 4);
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void BackToLive_Click(object sender, RoutedEventArgs e)
    {
        SessionsFlyout.Hide();
        if (_liveStore is null) return;
        if (_viewStore != _liveStore) _viewStore?.Dispose();
        _viewStore = _liveStore;
        ViewModel.IsReviewingPast = false;
        LoadShots(_liveStore);
    }

    private async void EndSession_Click(object sender, RoutedEventArgs e)
    {
        if (_liveStore is null) return;
        int shots = _liveStore.Data.Shots.Count;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "End this session?",
            Content = $"“{_liveStore.Name}” has {shots} shot{(shots == 1 ? "" : "s")}. It stays in your sessions folder, and a new session starts right away.",
            PrimaryButtonText = "End session",
            CloseButtonText = "Keep going",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        string endedName = _liveStore.Name;
        _liveStore.End();
        bool viewingLive = _viewStore == _liveStore;
        var old = _liveStore;
        _liveStore = SessionStore.Create(_settings.Current.SessionsRoot);
        _pipeline.Session = _liveStore;
        UpdateSessionCameras();
        if (viewingLive)
        {
            _viewStore = _liveStore;
            LoadShots(_liveStore);
            SetMode(StageMode.Live);
        }
        old.Dispose();
        RebuildLiveSurfaces();
        RefreshPastSessions();
        ShowToast($"Saved “{endedName}”. New session started.", InfoBarSeverity.Success, 3);
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        string folder = (_viewStore?.Folder) ?? _settings.Current.SessionsRoot;
        Directory.CreateDirectory(folder);
        await Launcher.LaunchFolderPathAsync(folder);
    }

    private async void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = WindowInterop.WithOwner(new FolderPicker(), this);
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        _settings.Current.SessionsRoot = folder.Path;
        _settings.Save();
        SessionsRootText.Text = folder.Path;
        ShowToast("New sessions will be saved there. End this session to start one in the new folder.", InfoBarSeverity.Informational, 4);
    }

    // ================================================================= stage & modes

    private void ModeBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_updatingUi) return;
        var mode = sender.SelectedItem == ReplayModeItem ? StageMode.Replay
            : sender.SelectedItem == CompareModeItem ? StageMode.Compare
            : StageMode.Live;
        SetMode(mode);
    }

    private void SetMode(StageMode mode)
    {
        bool reload = ViewModel.Mode != mode && (mode == StageMode.Compare || ViewModel.Mode == StageMode.Compare);
        ViewModel.Mode = mode;
        _updatingUi = true;
        ModeBar.SelectedItem = mode switch
        {
            StageMode.Replay => ReplayModeItem,
            StageMode.Compare => CompareModeItem,
            _ => LiveModeItem,
        };
        _updatingUi = false;

        if (mode == StageMode.Replay && _replay.Tracks.Count == 0 && ViewModel.SelectedShot is null &&
            ViewModel.Shots.LastOrDefault() is ShotViewModel last)
            SelectShot(last);
        else if (reload) LoadReplay();

        if (mode == StageMode.Live) _replay.Pause();
        else if (_replay.Tracks.Count > 0 && !_replay.IsPlaying) _replay.Play();
        UpdateStage();
    }

    private void UpdateStage()
    {
        var mode = ViewModel.Mode;
        bool anyCamera = _live.Count > 0;
        bool hasTracks = _replay.Tracks.Count > 0;

        LiveTiles.Visibility = mode == StageMode.Live ? Visibility.Visible : Visibility.Collapsed;
        ReplayTiles.Visibility = mode != StageMode.Live ? Visibility.Visible : Visibility.Collapsed;
        LiveInsetHost.Visibility = mode != StageMode.Live && _inset is not null && _settings.Current.ShowLiveInset
            ? Visibility.Visible : Visibility.Collapsed;
        Transport.Visibility = mode != StageMode.Live && hasTracks ? Visibility.Visible : Visibility.Collapsed;
        StripHint.Visibility = ViewModel.Shots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        GhostToggle.Visibility = mode == StageMode.Compare ? Visibility.Visible : Visibility.Collapsed;

        (bool empty, string title, string subtitle) = mode switch
        {
            StageMode.Live when !anyCamera => (true, "No camera connected",
                "Plug in a webcam, or open Settings to turn one on. Phones can join as cameras from the SwingLoop app."),
            StageMode.Replay when !hasTracks => (true, "No swings yet",
                "Step into view and hit a ball. SwingLoop saves the swing and loops it here until your next one."),
            StageMode.Compare when ViewModel.CompareShot is null => (true, "Pick two shots",
                "Select a shot, then Ctrl+click (or right-click) another one in the strip below."),
            _ => (false, "", ""),
        };
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = title;
        EmptySubtitle.Text = subtitle;
    }

    // ================================================================= transport

    private void PlayPause_Click(object sender, RoutedEventArgs e) => _replay.Toggle();
    private void StepBack_Click(object sender, RoutedEventArgs e) => _replay.StepFrames(-1);
    private void StepForward_Click(object sender, RoutedEventArgs e) => _replay.StepFrames(1);
    private void Impact_Click(object sender, RoutedEventArgs e) => _replay.JumpToImpact();

    private void SpeedBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_updatingUi || sender.SelectedItem?.Tag is not string tag) return;
        SetSpeed(double.Parse(tag, System.Globalization.CultureInfo.InvariantCulture));
    }

    private void SetSpeed(double rate)
    {
        _replay.Rate = rate;
        if (_castPlayer is not null) _castPlayer.PlaybackSession.PlaybackRate = rate;
        SyncSpeedBar();
    }

    private void SyncSpeedBar()
    {
        _updatingUi = true;
        SpeedBar.SelectedItem = SpeedBar.Items.OfType<SelectorBarItem>()
            .FirstOrDefault(i => Math.Abs(double.Parse((string)i.Tag, System.Globalization.CultureInfo.InvariantCulture) - _replay.Rate) < 0.001);
        _updatingUi = false;
    }

    private void CycleSpeed(int direction)
    {
        double[] speeds = [1, 0.5, 0.25, 0.125];
        int i = Array.FindIndex(speeds, s => Math.Abs(s - _replay.Rate) < 0.001);
        SetSpeed(speeds[Math.Clamp((i < 0 ? 0 : i) + direction, 0, speeds.Length - 1)]);
    }

    private void UpdateScrubber()
    {
        _updatingUi = true;
        Scrubber.Maximum = _replay.DurationSeconds;
        Scrubber.Value = _replay.PositionSeconds;
        _updatingUi = false;
        double rel = (_replay.Position - _replay.ImpactPosition).TotalSeconds;
        TimeText.Text = $"{(rel >= 0 ? "+" : "−")}{Math.Abs(rel):0.000}s";
    }

    private void Scrubber_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingUi) return;
        _replay.Seek(TimeSpan.FromSeconds(e.NewValue));
    }

    private void PositionImpactMarker()
    {
        double w = Scrubber.ActualWidth;
        if (w <= 0 || _replay.Duration <= TimeSpan.Zero) return;
        double ratio = _replay.ImpactPosition.TotalSeconds / _replay.DurationSeconds;
        const double thumbInset = 10;
        Canvas.SetLeft(ImpactMarker, thumbInset + ratio * (w - 2 * thumbInset) - ImpactMarker.Width / 2);
    }

    private void Ghost_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CompareOverlay = GhostToggle.IsChecked == true;
        LoadReplay();
    }

    private void Star_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedShot is not ShotViewModel shot) return;
        shot.IsStarred = StarToggle.IsChecked == true;
        StarGlyph.Glyph = shot.IsStarred ? "" : "";
    }

    private void SaveComment()
    {
        if (_updatingUi || ViewModel.SelectedShot is not ShotViewModel shot || shot.Comment == CommentBox.Text) return;
        shot.Comment = CommentBox.Text;
        shot.Save();
    }

    private void CommentBox_LostFocus(object sender, RoutedEventArgs e) => SaveComment();

    private void CommentBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        SaveComment();
        Root.Focus(FocusState.Programmatic);
        e.Handled = true;
    }

    private void SaveClub(string club)
    {
        if (_updatingUi || ViewModel.SelectedShot is not ShotViewModel shot || shot.Club == club) return;
        shot.Club = club;
        shot.Save();
    }

    private void ClubBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ClubBox.SelectedItem is string club) SaveClub(club);
    }

    private void ClubBox_TextSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args) => SaveClub(args.Text.Trim());

    private async void DeleteShot_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedShot is ShotViewModel shot) await DeleteShotAsync(shot);
    }

    // ================================================================= drawing

    private void BuildSwatches()
    {
        foreach (var hex in _settings.Current.RecentColors)
        {
            var b = new Button
            {
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(AnnotationCanvas.ParseColor(hex)),
                BorderThickness = new Thickness(hex == ViewModel.Color ? 3 : 1),
                BorderBrush = new SolidColorBrush(Colors.White),
                Tag = hex,
            };
            ToolTipService.SetToolTip(b, "Drawing color");
            b.Click += (_, _) => SetColor(hex);
            Swatches.Items.Add(b);
        }
    }

    private void SetColor(string hex)
    {
        ViewModel.Color = hex;
        foreach (var b in Swatches.Items.OfType<Button>())
            b.BorderThickness = new Thickness((string)b.Tag == hex ? 3 : 1);
        foreach (var c in AllCanvases())
        {
            c.Color = hex;
            c.ApplyColorToSelection(hex);
        }
    }

    private IEnumerable<AnnotationCanvas> AllCanvases() =>
        _live.Values.Select(v => v.Surface.Annotations).Concat(_replaySurfaces.Select(s => s.Annotations));

    private void ApplyDrawingState(AnnotationCanvas c)
    {
        c.Tool = ViewModel.Tool;
        c.Color = ViewModel.Color;
        c.StrokeWidth = (float)ViewModel.StrokeWidth;
        c.Dashed = DashedSwitch.IsOn;
        if (!_live.Values.Any(v => v.Surface.Annotations == c)) c.DrawPersistent = ViewModel.DrawPersistent;
    }

    private void SetTool(DrawTool tool)
    {
        ViewModel.Tool = ViewModel.Tool == tool ? DrawTool.None : tool;
        foreach (var t in new[] { ToolSelect, ToolLine, ToolArrow, ToolCircle, ToolRect, ToolAngle, ToolFree })
            t.IsChecked = (string)t.Tag == ViewModel.Tool.ToString();
        foreach (var c in AllCanvases()) ApplyDrawingState(c);
        // Drawing on a moving picture is frustrating: pause while a pen tool is active.
        if (ViewModel.Tool is not DrawTool.None and not DrawTool.Select && _replay.IsPlaying) _replay.Pause();
    }

    private void Tool_Click(object sender, RoutedEventArgs e) =>
        SetTool(Enum.Parse<DrawTool>((string)((FrameworkElement)sender).Tag));

    private void StrokeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        ViewModel.StrokeWidth = e.NewValue;
        foreach (var c in AllCanvases())
        {
            c.StrokeWidth = (float)e.NewValue;
            c.ApplyStrokeToSelection((float)e.NewValue);
        }
    }

    private void DashedSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        foreach (var c in AllCanvases()) c.Dashed = DashedSwitch.IsOn;
    }

    private void PersistToggle_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.DrawPersistent = PersistToggle.IsChecked == true;
        foreach (var c in AllCanvases()) ApplyDrawingState(c);
        ShowToast(ViewModel.DrawPersistent
            ? "New drawings stay on every shot from that camera."
            : "New drawings belong to this shot only.", InfoBarSeverity.Informational, 2);
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => _lastEditedCanvas?.Undo();

    private void DeleteShape_Click(object sender, RoutedEventArgs e)
    {
        foreach (var c in AllCanvases().Where(c => c.HasSelection)) c.DeleteSelected();
    }

    private void TogglePersist_Click(object sender, RoutedEventArgs e)
    {
        foreach (var c in AllCanvases().Where(c => c.HasSelection)) c.ToggleSelectedPersistence();
    }

    private void ClearShot_Click(object sender, RoutedEventArgs e)
    {
        foreach (var s in _replaySurfaces) s.Annotations.ClearShot();
    }

    private void ClearPersistent_Click(object sender, RoutedEventArgs e)
    {
        foreach (var c in AllCanvases()) c.ClearPersistent();
    }

    private void OnShapesChanged(AnnotationCanvas canvas, bool persistent)
    {
        _lastEditedCanvas = canvas;
        if (!_canvasTargets.TryGetValue(canvas, out var target)) return;
        var (store, shot) = target;
        try
        {
            if (persistent)
            {
                store.SetPersistentAnnotations(canvas.MergeInto(store.Data.PersistentAnnotations, true));
            }
            else if (shot is not null)
            {
                shot.Annotations = canvas.MergeInto(shot.Annotations, false);
                store.UpdateShot(shot);
            }
        }
        catch (Exception ex)
        {
            ShowToast($"Could not save drawing: {ex.Message}", InfoBarSeverity.Error);
        }

        // Mirror the change onto every other view of the same camera and session.
        foreach (var (other, t) in _canvasTargets)
        {
            if (other == canvas || t.Store != store || other.CameraId != canvas.CameraId) continue;
            if (!persistent && t.Shot?.Id != shot?.Id) continue;
            other.SetShapes(t.Shot?.Annotations ?? [], store.Data.PersistentAnnotations);
        }
        foreach (var (surface, _, _) in _pipSurfaces)
            if (surface.CameraId == canvas.CameraId)
                surface.Annotations.SetShapes(shot?.Annotations ?? surface.Annotations.ShotShapes, store.Data.PersistentAnnotations);
    }

    // ================================================================= capture, export, share

    private void SaveSwing_Click(object sender, RoutedEventArgs e) => _pipeline.TriggerManual();

    private (ShotViewModel Shot, ClipDto Clip, List<ShapeDto> Shapes)? CurrentExportTarget()
    {
        if (ViewModel.SelectedShot is not ShotViewModel shot || shot.Dto.Clips.Count == 0) return null;
        // The angle on screen first (the first replay tile), else the shot's primary clip.
        var clip = _replay.Tracks.FirstOrDefault(t => t.Shot.Id == shot.Dto.Id)?.Clip ?? shot.Dto.Clips[0];
        var shapes = shot.Dto.Annotations.Where(s => s.CameraId is null || s.CameraId == clip.CameraId)
            .Concat(shot.Store.Data.PersistentAnnotations.Where(s => s.CameraId is null || s.CameraId == clip.CameraId))
            .ToList();
        return (shot, clip, shapes);
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentExportTarget() is not (ShotViewModel shot, ClipDto clip, List<ShapeDto> shapes)) return;
        string tag = (string)((FrameworkElement)sender).Tag;
        bool share = tag.StartsWith("share");
        bool still = tag.Contains("still");
        bool marked = tag.EndsWith("marked");
        string ext = still ? ".png" : ".mp4";
        string name = ExportService.SuggestedName(shot.Dto, clip, marked, ext);

        string outPath;
        if (share)
        {
            outPath = Path.Combine(ExportService.ExportFolder, name);
        }
        else
        {
            var picker = WindowInterop.WithOwner(new FileSavePicker(), this);
            picker.SuggestedStartLocation = still ? PickerLocationId.PicturesLibrary : PickerLocationId.VideosLibrary;
            picker.SuggestedFileName = Path.GetFileNameWithoutExtension(name);
            picker.FileTypeChoices.Add(still ? "PNG image" : "MP4 video", [ext]);
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            outPath = file.Path;
        }

        ShowToast(still ? "Saving frame…" : "Exporting video…", InfoBarSeverity.Informational, 0);
        try
        {
            string src = shot.ClipPath(clip);
            var clipPosition = _replay.Position + (_replay.Tracks.FirstOrDefault(t => t.Clip == clip)?.Offset ?? TimeSpan.Zero);
            if (still) await ExportService.ExportStillAsync(src, clip, clipPosition, shapes, marked, outPath);
            else await ExportService.ExportClipAsync(src, clip, shapes, marked, outPath);
        }
        catch (Exception ex)
        {
            ShowToast($"Export failed: {ex.Message}", InfoBarSeverity.Error);
            return;
        }

        if (share)
        {
            Toast.IsOpen = false;
            var file = await StorageFile.GetFileFromPathAsync(outPath);
            var dtm = WindowInterop.DataTransferManagerFor(this);
            void OnRequested(DataTransferManager s, DataRequestedEventArgs args)
            {
                args.Request.Data.Properties.Title = $"SwingLoop shot #{shot.Number}";
                if (!string.IsNullOrWhiteSpace(shot.Comment)) args.Request.Data.Properties.Description = shot.Comment;
                args.Request.Data.SetStorageItems([file]);
                s.DataRequested -= OnRequested;
            }
            dtm.DataRequested += OnRequested;
            WindowInterop.ShowShareUI(this);
        }
        else
        {
            ShowToast($"Saved {Path.GetFileName(outPath)}", InfoBarSeverity.Success, 3);
        }
    }

    // ================================================================= picture-in-picture

    private void Pip_Click(object sender, RoutedEventArgs e) => TogglePip();

    private void TogglePip()
    {
        if (_pip is not null)
        {
            _pip.Close();
            return;
        }
        _pip = new PipWindow(_settings.Current.Pip);
        _pip.ContentRequested += content =>
        {
            _settings.Current.Pip.Content = content;
            _settings.Save();
            SyncPipCombo();
            RebuildPip(replayPlayersDisposed: false);
        };
        _pip.PlayPauseRequested += () => _replay.Toggle();
        _pip.SpeedRequested += () => CycleSpeed(1 - (Math.Abs(_replay.Rate - 0.125) < 0.001 ? 4 : 0));
        _pip.BoundsChanged += () => _settings.Save();
        _pip.Closed += (_, _) =>
        {
            ReleasePipSurfaces(replayPlayersDisposed: false);
            _pip = null;
        };
        RebuildPip(replayPlayersDisposed: false);
        _pip.Activate();
    }

    private void ReleasePipSurfaces(bool replayPlayersDisposed)
    {
        foreach (var (surface, player, owned) in _pipSurfaces)
        {
            surface.Player = null;
            if (owned) player.Dispose();
            else if (!replayPlayersDisposed) _replay.ReleasePlayer(player);
        }
        _pipSurfaces.Clear();
        _pip?.Tiles.Children.Clear();
    }

    private void RebuildPip(bool replayPlayersDisposed)
    {
        if (_pip is null) return;
        ReleasePipSurfaces(replayPlayersDisposed);
        var pip = _settings.Current.Pip;

        if (pip.Content is "replay" or "both")
        {
            var tracks = _settings.Current.MultiReplayView ? _replay.Tracks : _replay.Tracks.Take(1);
            foreach (var track in tracks)
            {
                var surface = CreateReplaySurface(track, _viewStore, interactive: false);
                surface.Annotations.Visibility = pip.ShowAnnotations ? Visibility.Visible : Visibility.Collapsed;
                surface.Label = "";
                _pip.Tiles.Children.Add(surface);
                _pipSurfaces.Add((surface, surface.Player!, false));
            }
        }
        if (pip.Content is "live" or "both" ||
            (pip.Content == "replay" && _replay.Tracks.Count == 0))
        {
            var cam = ViewModel.Cameras.Where(c => c.Source is not null).OrderByDescending(c => c.Settings.IsDetector).FirstOrDefault();
            if (cam is not null)
            {
                var player = new MediaPlayer { RealTimePlayback = true, IsMuted = true, AutoPlay = true, Source = cam.Source!.CreatePreviewSource() };
                var surface = NewSurface(cam.Id, cam.Source.Width, cam.Source.Height, cam.Settings.Rotation, cam.Settings);
                surface.Annotations.Tool = DrawTool.None;
                surface.Player = player;
                surface.Label = pip.Content == "both" ? "LIVE" : "";
                _pip.Tiles.Children.Add(surface);
                _pipSurfaces.Add((surface, player, true));
                player.Play();
            }
        }
        _pip.ContentAspect = _pipSurfaces.Count == 0 ? 16f / 9f : _pipSurfaces.Sum(s => s.Surface.DisplayAspect);
        _pip.IsPlaying = _replay.IsPlaying;
    }

    // ================================================================= casting

    private sealed record CastDeviceItem(DeviceInformation Info)
    {
        public override string ToString() => Info.Name;
    }

    private async void CastFlyout_Opening(object sender, object e)
    {
        CastStatus.Text = _cast.IsCasting ? $"Casting to {_cast.DeviceName}" : "Looking for TVs and adapters…";
        StopCastButton.Visibility = _cast.IsCasting ? Visibility.Visible : Visibility.Collapsed;
        try
        {
            var devices = await _cast.FindDevicesAsync();
            CastDevices.ItemsSource = devices.Select(d => new CastDeviceItem(d)).ToList();
            if (!_cast.IsCasting)
                CastStatus.Text = devices.Count == 0 ? "No cast devices found on this network." : "Pick a device to cast the replay.";
        }
        catch (Exception ex)
        {
            CastStatus.Text = ex.Message;
        }
    }

    private async void CastDevices_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not CastDeviceItem item) return;
        _castPlayer ??= new MediaPlayer { IsLoopingEnabled = true, IsMuted = true };
        UpdateCastSource();
        CastStatus.Text = $"Connecting to {item.Info.Name}…";
        string? error = await _cast.CastAsync(item.Info, _castPlayer);
        CastStatus.Text = error ?? $"Casting to {item.Info.Name}";
        StopCastButton.Visibility = error is null ? Visibility.Visible : Visibility.Collapsed;
        if (error is null) _castPlayer.Play();
    }

    private void UpdateCastSource()
    {
        if (_castPlayer is null || !_cast.IsCasting && _castPlayer.Source is not null) return;
        var track = _replay.Tracks.FirstOrDefault();
        if (track is null) return;
        _castPlayer.Source = MediaSource.CreateFromUri(new Uri(track.Path));
        _castPlayer.PlaybackSession.PlaybackRate = _replay.Rate;
        _castPlayer.Play();
    }

    private async void StopCast_Click(object sender, RoutedEventArgs e)
    {
        await _cast.StopAsync();
        _castPlayer?.Pause();
        CastStatus.Text = "Stopped.";
        StopCastButton.Visibility = Visibility.Collapsed;
    }

    private void Mirror_Click(object sender, RoutedEventArgs e)
    {
        CastFlyout.Hide();
        WindowInterop.OpenSystemCastFlyout();
    }

    // ================================================================= settings pane

    private void SettingsToggle_Changed(object sender, RoutedEventArgs e) => Split.IsPaneOpen = SettingsToggle.IsChecked == true;

    private void LoadSettingsIntoUi()
    {
        var s = _settings.Current;
        _updatingUi = true;
        UsePoseSwitch.IsOn = s.UsePose;
        UseAudioSwitch.IsOn = s.UseAudio;
        RequireImpactSwitch.IsOn = s.RequireImpactSound;
        SavePracticeSwitch.IsOn = s.SavePracticeSwings;
        PreRollSlider.Value = s.PreRollSeconds;
        PostRollSlider.Value = s.PostRollSeconds;
        QualityCombo.SelectedItem = QualityCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.MaxCaptureHeight.ToString());
        AutoReplaySwitch.IsOn = s.AutoReplay;
        DefaultSpeedCombo.SelectedItem = DefaultSpeedCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => Math.Abs(double.Parse((string)i.Tag, System.Globalization.CultureInfo.InvariantCulture) - s.DefaultSpeed) < 0.001);
        PipAspectSwitch.IsOn = s.Pip.LockAspect;
        PipDrawingsSwitch.IsOn = s.Pip.ShowAnnotations;
        MultiLiveItem.IsChecked = s.MultiLiveView;
        MultiReplayItem.IsChecked = s.MultiReplayView;
        LiveInsetItem.IsChecked = s.ShowLiveInset;
        SessionsRootText.Text = s.SessionsRoot;
        AboutText.Text = $"SwingLoop 0.1 · core {CoreIds.Version}";
        _updatingUi = false;
        SyncPipCombo();
        SyncSpeedBar();
    }

    private void SyncPipCombo()
    {
        _updatingUi = true;
        PipContentCombo.SelectedItem = PipContentCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _settings.Current.Pip.Content);
        _updatingUi = false;
    }

    private void Detection_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingUi) return;
        var s = _settings.Current;
        s.UsePose = UsePoseSwitch.IsOn;
        s.UseAudio = UseAudioSwitch.IsOn;
        s.RequireImpactSound = RequireImpactSwitch.IsOn;
        s.SavePracticeSwings = SavePracticeSwitch.IsOn;
        _settings.Save();
        _pipeline.ApplyDetectorConfig();
    }

    private void Roll_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingUi) return;
        _settings.Current.PreRollSeconds = PreRollSlider.Value;
        _settings.Current.PostRollSeconds = PostRollSlider.Value;
        _settings.Save();
        _pipeline.ApplyDetectorConfig();
    }

    private async void Quality_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi || QualityCombo.SelectedItem is not ComboBoxItem item) return;
        _settings.Current.MaxCaptureHeight = int.Parse((string)item.Tag);
        _settings.Save();
        // Restart cameras with the new format.
        foreach (var vm in ViewModel.Cameras.Where(c => c.Source is not null).ToList())
        {
            await StopCameraAsync(vm);
            await StartCameraAsync(vm);
        }
        RebuildLiveSurfaces();
    }

    private void ReplaySwitch_Toggled(object sender, RoutedEventArgs e) => SaveReplaySettings();
    private void ReplayCombo_Changed(object sender, SelectionChangedEventArgs e) => SaveReplaySettings();

    private void SaveReplaySettings()
    {
        if (_updatingUi) return;
        _settings.Current.AutoReplay = AutoReplaySwitch.IsOn;
        if (DefaultSpeedCombo.SelectedItem is ComboBoxItem item)
            _settings.Current.DefaultSpeed = double.Parse((string)item.Tag, System.Globalization.CultureInfo.InvariantCulture);
        _settings.Save();
    }

    private void PipSwitch_Toggled(object sender, RoutedEventArgs e) => SavePipSettings();
    private void PipCombo_Changed(object sender, SelectionChangedEventArgs e) => SavePipSettings();

    private void SavePipSettings()
    {
        if (_updatingUi) return;
        var pip = _settings.Current.Pip;
        if (PipContentCombo.SelectedItem is ComboBoxItem item) pip.Content = (string)item.Tag;
        pip.LockAspect = PipAspectSwitch.IsOn;
        pip.ShowAnnotations = PipDrawingsSwitch.IsOn;
        _settings.Save();
        RebuildPip(replayPlayersDisposed: false);
    }

    private void Layout_Changed(object sender, RoutedEventArgs e)
    {
        var s = _settings.Current;
        s.MultiLiveView = MultiLiveItem.IsChecked;
        s.MultiReplayView = MultiReplayItem.IsChecked;
        s.ShowLiveInset = LiveInsetItem.IsChecked;
        _settings.Save();
        RebuildLiveSurfaces();
        if (ViewModel.SelectedShot is not null) LoadReplay();
    }

    private void Aspect_Click(object sender, RoutedEventArgs e)
    {
        _settings.Current.UniformAspect = (string)((FrameworkElement)sender).Tag;
        _settings.Save();
        float aspect = AppSettings.ParseAspect(_settings.Current.UniformAspect);
        LiveTiles.UniformAspect = aspect;
        ReplayTiles.UniformAspect = aspect;
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e) => await RescanCamerasAsync();

    private static CameraViewModel? CameraOf(object sender) => (sender as FrameworkElement)?.Tag as CameraViewModel;

    private async void CameraEnabled_Toggled(object sender, RoutedEventArgs e)
    {
        if (CameraOf(sender) is not CameraViewModel vm || sender is not ToggleSwitch sw || sw.IsOn == (vm.Source is not null)) return;
        vm.Settings.Enabled = sw.IsOn;
        _settings.Save();
        if (sw.IsOn) await StartCameraAsync(vm);
        else await StopCameraAsync(vm);
        EnsureDetectorCamera();
        _pipeline.ApplyDetectorConfig();
        UpdateSessionCameras();
        RebuildLiveSurfaces();
    }

    private void CameraPosition_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CameraOf(sender) is not CameraViewModel vm || ((ComboBox)sender).SelectedItem is not string pos || pos == vm.Settings.Position) return;
        vm.Settings.Position = pos;
        _settings.Save();
        UpdateSessionCameras();
        RebuildLiveSurfaces();
    }

    private void CameraRotation_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CameraOf(sender) is not CameraViewModel vm || ((ComboBox)sender).SelectedItem is not int rot || rot == vm.Settings.Rotation) return;
        vm.Settings.Rotation = rot;
        _settings.Save();
        _pipeline.ApplyDetectorConfig();
        RefreshCameraGeometry(vm);
    }

    private void CameraFine_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (CameraOf(sender) is not CameraViewModel vm || Math.Abs(vm.Settings.FineRotation - e.NewValue) < 0.01) return;
        vm.Settings.FineRotation = (float)e.NewValue;
        _settings.Save();
        RefreshCameraGeometry(vm);
    }

    private async void CameraZoom_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (CameraOf(sender) is not CameraViewModel vm || Math.Abs(vm.Settings.Zoom - e.NewValue) < 0.01) return;
        vm.Settings.Zoom = (float)e.NewValue;
        _settings.Save();
        if (vm.Source?.SupportsHardwareZoom == true) await vm.Source.SetHardwareZoomAsync(vm.Settings.Zoom);
        else if (_live.TryGetValue(vm.Id, out var entry)) entry.Surface.SetZoom(vm.Settings.Zoom);
    }

    private void CameraFlag_Click(object sender, RoutedEventArgs e)
    {
        if (CameraOf(sender) is not CameraViewModel vm || sender is not CheckBox cb) return;
        bool on = cb.IsChecked == true;
        switch (cb.Content as string)
        {
            case "Mirror": vm.Settings.Mirror = on; break;
            case "Show in live view": vm.Settings.ShowLive = on; break;
            case "Show in replay": vm.Settings.ShowReplay = on; break;
        }
        _settings.Save();
        RebuildLiveSurfaces();
        if (ViewModel.SelectedShot is not null) LoadReplay();
    }

    private void CameraDetector_Checked(object sender, RoutedEventArgs e)
    {
        if (CameraOf(sender) is not CameraViewModel vm || vm.Settings.IsDetector) return;
        foreach (var c in ViewModel.Cameras) c.Settings.IsDetector = c == vm;
        _settings.Save();
        _pipeline.ApplyDetectorConfig();
        RebuildInset();
    }

    private void RefreshCameraGeometry(CameraViewModel vm)
    {
        var s = vm.Settings;
        if (_live.TryGetValue(vm.Id, out var entry) && vm.Source is not null)
            entry.Surface.SetSource(vm.Source.Width, vm.Source.Height, s.Rotation, s.FineRotation, s.Mirror);
        foreach (var r in _replaySurfaces.Concat(_pipSurfaces.Select(p => p.Surface)).Where(r => r.CameraId == vm.Id))
            r.SetSource(r.SourceWidth, r.SourceHeight, s.Rotation, s.FineRotation, s.Mirror);
        if (_inset?.CameraId == vm.Id) RebuildInset();
        LiveTiles.InvalidateArrange();
        ReplayTiles.InvalidateArrange();
    }

    // ================================================================= keyboard & feedback

    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox or ComboBox { IsEditable: true }) return;
        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        bool handled = true;
        switch (e.Key)
        {
            case VirtualKey.Space: _replay.Toggle(); break;
            case VirtualKey.Left: _replay.StepFrames(shift ? -5 : -1); break;
            case VirtualKey.Right: _replay.StepFrames(shift ? 5 : 1); break;
            case VirtualKey.Up: CycleSpeed(-1); break;
            case VirtualKey.Down: CycleSpeed(1); break;
            case VirtualKey.I: _replay.JumpToImpact(); break;
            case VirtualKey.M: _pipeline.TriggerManual(); break;
            case VirtualKey.P: TogglePip(); break;
            case VirtualKey.S when ViewModel.SelectedShot is ShotViewModel shot:
                shot.IsStarred = !shot.IsStarred;
                ShowShotDetails(shot);
                break;
            case VirtualKey.Z when ctrl: _lastEditedCanvas?.Undo(); break;
            case VirtualKey.Delete: DeleteShape_Click(this, new RoutedEventArgs()); break;
            case VirtualKey.Escape: SetTool(DrawTool.None); break;
            case VirtualKey.V: SetTool(DrawTool.Select); break;
            case VirtualKey.L: SetTool(DrawTool.Line); break;
            case VirtualKey.A: SetTool(DrawTool.Arrow); break;
            case VirtualKey.C: SetTool(DrawTool.Circle); break;
            case VirtualKey.B: SetTool(DrawTool.Rectangle); break;
            case VirtualKey.G: SetTool(DrawTool.Angle); break;
            case VirtualKey.F: SetTool(DrawTool.Freehand); break;
            case VirtualKey.K:
                PersistToggle.IsChecked = PersistToggle.IsChecked != true;
                PersistToggle_Click(this, new RoutedEventArgs());
                break;
            case VirtualKey.F11: ToggleFullScreen(); break;
            case VirtualKey.Number1: SetSpeed(1); break;
            case VirtualKey.Number2: SetSpeed(0.5); break;
            case VirtualKey.Number3: SetSpeed(0.25); break;
            case VirtualKey.Number4: SetSpeed(0.125); break;
            default: handled = false; break;
        }
        e.Handled = handled;
    }

    private void ToggleFullScreen()
    {
        AppWindow.SetPresenter(AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen
            ? AppWindowPresenterKind.Default
            : AppWindowPresenterKind.FullScreen);
    }

    private void UpdateMeters()
    {
        if (!_pipeline.HasAudio) return;
        var (level, floor) = _pipeline.AudioLevels;
        AudioMeter.Value = Math.Clamp(level - floor, 0, 60);
        if (_pip is not null) _pip.IsPlaying = _replay.IsPlaying;
    }

    private CancellationTokenSource? _toastCts;

    private async void ShowToast(string message, InfoBarSeverity severity, double seconds = 5)
    {
        _toastCts?.Cancel();
        var cts = _toastCts = new CancellationTokenSource();
        Toast.Message = message;
        Toast.Severity = severity;
        Toast.IsOpen = true;
        if (seconds <= 0) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), cts.Token);
            Toast.IsOpen = false;
        }
        catch (TaskCanceledException)
        {
        }
    }

    // x:Bind helpers
    public static Visibility Vis(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public Brush GolferBrush(bool present) =>
        (Brush)Application.Current.Resources[present ? "GolferBrush" : "IdleBrush"];
}
