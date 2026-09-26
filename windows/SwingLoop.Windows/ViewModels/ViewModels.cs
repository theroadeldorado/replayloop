using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using SwingLoop.Capture;
using SwingLoop.Controls;
using SwingLoop.Interop;
using SwingLoop.Models;
using SwingLoop.Services;

namespace SwingLoop.ViewModels;

public enum StageMode { Live, Replay, Compare }

public sealed partial class ShotViewModel : ObservableObject
{
    public ShotViewModel(SessionStore store, ShotDto dto)
    {
        Store = store;
        Dto = dto;
        _isStarred = dto.Starred;
        _comment = dto.Comment;
        _club = dto.Club;
    }

    public SessionStore Store { get; }
    public ShotDto Dto { get; private set; }

    public int Number => Dto.Number;
    public string Title => $"#{Dto.Number}";
    public bool IsPractice => Dto.Tags.Contains("practice");
    public bool HasComment => !string.IsNullOrWhiteSpace(Comment);

    public string TempoText => Dto.Timing.TempoRatio > 0.5 ? $"{Dto.Timing.TempoRatio:0.0} : 1" : "";

    public string TimeText => DateTime.TryParse(Dto.CreatedAt, out var t) ? t.ToLocalTime().ToString("h:mm:ss tt") : "";

    public string SourceText => Dto.Source switch
    {
        "manual" => "Saved manually",
        "audio" => "Impact sound",
        _ => Dto.ImpactConfirmed ? "Swing + impact" : "Swing",
    };

    private BitmapImage? _thumbnail;
    public BitmapImage? Thumbnail
    {
        get
        {
            if (_thumbnail is null && Dto.Clips.Count > 0)
            {
                string path = Store.ThumbnailPath(Dto, Dto.Clips[0]);
                if (File.Exists(path)) _thumbnail = new BitmapImage(new Uri(path)) { DecodePixelWidth = 240 };
            }
            return _thumbnail;
        }
    }

    [ObservableProperty] private bool _isStarred;
    [ObservableProperty] private string _comment = "";
    [ObservableProperty] private string _club = "";
    [ObservableProperty] private bool _isCompareTarget;

    public Visibility StarVisibility => IsStarred ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CommentVisibility => HasComment ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PracticeVisibility => IsPractice ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CompareVisibility => IsCompareTarget ? Visibility.Visible : Visibility.Collapsed;

    partial void OnIsStarredChanged(bool value)
    {
        Dto.Starred = value;
        Save();
        OnPropertyChanged(nameof(StarVisibility));
    }

    partial void OnCommentChanged(string value)
    {
        Dto.Comment = value;
        OnPropertyChanged(nameof(HasComment));
        OnPropertyChanged(nameof(CommentVisibility));
    }

    partial void OnClubChanged(string value) => Dto.Club = value;

    partial void OnIsCompareTargetChanged(bool value) => OnPropertyChanged(nameof(CompareVisibility));

    public void SetAnnotations(List<ShapeDto> shapes)
    {
        Dto.Annotations = shapes;
        Save();
    }

    public void Save() => Store.UpdateShot(Dto);

    public string ClipPath(ClipDto clip) => Store.ClipPath(Dto, clip);
}

public sealed partial class CameraViewModel : ObservableObject
{
    public CameraViewModel(CameraDescriptor descriptor, CameraSettings settings)
    {
        Descriptor = descriptor;
        Settings = settings;
    }

    public CameraDescriptor Descriptor { get; }
    public CameraSettings Settings { get; }
    public ICameraSource? Source { get; set; }

    public string Id => Descriptor.Id;
    public string Name => Descriptor.Name;

    [ObservableProperty] private string _status = "";

    public static IReadOnlyList<string> Positions { get; } = ["face-on", "down-the-line", "rear", "overhead", "other"];
    public static IReadOnlyList<int> Rotations { get; } = [0, 90, 180, 270];

    public int PositionIndex => Math.Max(0, Positions.ToList().IndexOf(Settings.Position));
    public int RotationIndex => Math.Max(0, Rotations.ToList().IndexOf(Settings.Rotation));
    public double FineRotationValue => Settings.FineRotation;
    public double ZoomValue => Settings.Zoom;

    public string DisplayLabel => $"{Name} · {Settings.Position}";
    public string ZoomText => Source?.SupportsHardwareZoom == true ? "Optical zoom" : "Digital zoom";
}

public sealed partial class MainViewModel : ObservableObject
{
    public ObservableCollection<ShotViewModel> Shots { get; } = [];
    public ObservableCollection<CameraViewModel> Cameras { get; } = [];
    public ObservableCollection<SessionSummaryDto> PastSessions { get; } = [];

    [ObservableProperty] private ShotViewModel? _selectedShot;
    [ObservableProperty] private ShotViewModel? _compareShot;
    [ObservableProperty] private StageMode _mode = StageMode.Live;
    [ObservableProperty] private bool _compareOverlay;
    [ObservableProperty] private string _sessionName = "";
    [ObservableProperty] private bool _isReviewingPast;
    [ObservableProperty] private bool _golferInView;
    [ObservableProperty] private SwingPhase _phase = SwingPhase.NoGolfer;
    [ObservableProperty] private DrawTool _tool = DrawTool.None;
    [ObservableProperty] private string _color = "#FF3B30FF";
    [ObservableProperty] private double _strokeWidth = 4;
    [ObservableProperty] private bool _drawPersistent;
    [ObservableProperty] private bool _isCapturing;

    public string PhaseText => Phase switch
    {
        SwingPhase.NoGolfer => "Waiting for golfer",
        SwingPhase.Idle => "Golfer in view",
        SwingPhase.Address => "Address",
        SwingPhase.Backswing or SwingPhase.Downswing or SwingPhase.FollowThrough => "Swing!",
        SwingPhase.Cooldown => "Saving…",
        _ => "",
    };

    public string ShotCountText => Shots.Count == 1 ? "1 shot" : $"{Shots.Count} shots";

    partial void OnPhaseChanged(SwingPhase value) => OnPropertyChanged(nameof(PhaseText));

    public MainViewModel()
    {
        Shots.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShotCountText));
    }
}
