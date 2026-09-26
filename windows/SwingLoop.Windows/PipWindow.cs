using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SwingLoop.Controls;
using SwingLoop.Services;
using Windows.Graphics;

namespace SwingLoop;

/// <summary>
/// A small always-on-top, borderless, resizable window that floats the
/// replay (and/or live view) over a golf simulator or any other app. It
/// shares the main window's timeline, so it is always in sync with it.
/// </summary>
public sealed class PipWindow : Window
{
    private readonly PipSettings _settings;
    private readonly Grid _root = new() { Background = new SolidColorBrush(Colors.Black) };
    private readonly Border _bar;
    private readonly FontIcon _playGlyph = new() { Glyph = "", FontSize = 14 };
    private bool _adjusting;
    private PointInt32? _dragCursorStart;
    private PointInt32 _dragWindowStart;
    private CancellationTokenSource? _hideBar;

    public PipWindow(PipSettings settings)
    {
        _settings = settings;
        Title = "SwingLoop replay";
        Content = _root;
        _root.Children.Add(Tiles);

        _bar = BuildBar();
        _root.Children.Add(_bar);
        _root.PointerEntered += (_, _) => ShowBar();
        _root.PointerMoved += (_, _) => ShowBar();
        _root.PointerExited += (_, _) => HideBarSoon();

        var presenter = OverlappedPresenter.Create();
        presenter.IsAlwaysOnTop = true;
        presenter.IsMinimizable = false;
        presenter.IsMaximizable = false;
        presenter.IsResizable = true;
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        AppWindow.SetPresenter(presenter);

        AppWindow.Resize(new SizeInt32(Math.Max(200, settings.Width), Math.Max(120, settings.Height)));
        if (settings.X >= 0 && settings.Y >= 0) AppWindow.Move(new PointInt32(settings.X, settings.Y));
        AppWindow.Changed += OnAppWindowChanged;
    }

    public VideoTilesPanel Tiles { get; } = new() { Gap = 2 };

    public event Action<string>? ContentRequested;
    public event Action? PlayPauseRequested;
    public event Action? SpeedRequested;
    public event Action? BoundsChanged;

    private float _contentAspect = 16f / 9f;
    /// <summary>Width / height of what is shown, used to keep the window's shape when resizing.</summary>
    public float ContentAspect
    {
        get => _contentAspect;
        set
        {
            _contentAspect = value > 0 ? value : 16f / 9f;
            if (_settings.LockAspect) FitToAspect();
        }
    }

    public bool IsPlaying
    {
        set => _playGlyph.Glyph = value ? "" : "";
    }

    private Border BuildBar()
    {
        var drag = new Border
        {
            Width = 28,
            Background = new SolidColorBrush(Colors.Transparent),
            Child = new FontIcon { Glyph = "", FontSize = 14 },
        };
        ToolTipService.SetToolTip(drag, "Drag to move");
        drag.PointerPressed += OnDragPressed;
        drag.PointerMoved += OnDragMoved;
        drag.PointerReleased += (s, e) => { _dragCursorStart = null; ((UIElement)s).ReleasePointerCapture(e.Pointer); SaveBounds(); };

        Button Btn(object content, string tip, Action onClick)
        {
            var b = new Button
            {
                Content = content,
                Padding = new Thickness(8, 4, 8, 4),
                MinWidth = 0,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(6),
            };
            ToolTipService.SetToolTip(b, tip);
            b.Click += (_, _) => onClick();
            return b;
        }

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        panel.Children.Add(drag);
        panel.Children.Add(Btn(new TextBlock { Text = "Replay", FontSize = 12 }, "Show the replay", () => ContentRequested?.Invoke("replay")));
        panel.Children.Add(Btn(new TextBlock { Text = "Live", FontSize = 12 }, "Show the live camera", () => ContentRequested?.Invoke("live")));
        panel.Children.Add(Btn(new TextBlock { Text = "Both", FontSize = 12 }, "Replay and live side by side", () => ContentRequested?.Invoke("both")));
        panel.Children.Add(Btn(_playGlyph, "Play / pause", () => PlayPauseRequested?.Invoke()));
        panel.Children.Add(Btn(new TextBlock { Text = "Speed", FontSize = 12 }, "Cycle speed", () => SpeedRequested?.Invoke()));
        panel.Children.Add(Btn(new FontIcon { Glyph = "", FontSize = 12 }, "Close", Close));

        return new Border
        {
            Child = panel,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 0, 0),
            Padding = new Thickness(4, 2, 4, 2),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(ColorHelper.FromArgb(200, 22, 22, 26)),
            Opacity = 0,
        };
    }

    private void ShowBar()
    {
        _hideBar?.Cancel();
        _bar.Opacity = 1;
    }

    private async void HideBarSoon()
    {
        _hideBar?.Cancel();
        var cts = _hideBar = new CancellationTokenSource();
        try
        {
            await Task.Delay(1200, cts.Token);
            _bar.Opacity = 0;
        }
        catch (TaskCanceledException)
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint p);

    private static PointInt32 CursorPos()
    {
        GetCursorPos(out var p);
        return new PointInt32(p.X, p.Y);
    }

    private void OnDragPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragCursorStart = CursorPos();
        _dragWindowStart = AppWindow.Position;
        ((UIElement)sender).CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnDragMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragCursorStart is not PointInt32 start) return;
        var now = CursorPos();
        AppWindow.Move(new PointInt32(_dragWindowStart.X + now.X - start.X, _dragWindowStart.Y + now.Y - start.Y));
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange && _settings.LockAspect && !_adjusting) FitToAspect();
        if (args.DidSizeChange || args.DidPositionChange) SaveBounds();
    }

    private void FitToAspect()
    {
        var size = AppWindow.ClientSize;
        int wantHeight = (int)Math.Round(size.Width / _contentAspect);
        if (Math.Abs(wantHeight - size.Height) <= 2) return;
        _adjusting = true;
        AppWindow.ResizeClient(new SizeInt32(size.Width, Math.Max(90, wantHeight)));
        _adjusting = false;
    }

    private void SaveBounds()
    {
        _settings.X = AppWindow.Position.X;
        _settings.Y = AppWindow.Position.Y;
        _settings.Width = AppWindow.Size.Width;
        _settings.Height = AppWindow.Size.Height;
        BoundsChanged?.Invoke();
    }
}
