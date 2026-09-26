using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SwingLoop.Interop;
using Windows.Foundation;
using Windows.Media.Playback;

namespace SwingLoop.Controls;

/// <summary>
/// One video tile: a MediaPlayerElement plus its drawing layer, sharing a
/// transform so drawings stay glued to the picture through:
///   * mounting rotation (0/90/180/270) and fine leveling, auto-cropped so no
///     empty corners show,
///   * fit (letterbox) or fill (uniform crop) into the tile,
///   * digital zoom (wheel / pinch) and pan (right-drag / two fingers).
/// </summary>
public sealed class VideoSurface : Grid
{
    private readonly Grid _layer = new();
    private readonly MediaPlayerElement _video = new()
    {
        AreTransportControlsEnabled = false,
        Stretch = Stretch.Fill,
        IsHitTestVisible = false,
    };
    private readonly CompositeTransform _transform = new();
    private readonly RectangleGeometry _clip = new();
    private readonly Border _badge;
    private readonly TextBlock _badgeText = new() { FontSize = 12, Foreground = new SolidColorBrush(Colors.White) };
    private readonly Border _zoomBadge;
    private readonly TextBlock _zoomText = new() { FontSize = 12, Foreground = new SolidColorBrush(Colors.White) };
    private Point? _panStart;
    private Point _panOrigin;

    public VideoSurface()
    {
        Background = new SolidColorBrush(ColorHelper.FromArgb(255, 12, 12, 14));
        CornerRadius = new CornerRadius(10);
        Clip = _clip;

        _layer.Children.Add(_video);
        _layer.Children.Add(Annotations);
        _layer.RenderTransform = _transform;
        _layer.RenderTransformOrigin = new Point(0.5, 0.5);
        Children.Add(_layer);

        _badge = MakeBadge(_badgeText, HorizontalAlignment.Left);
        _zoomBadge = MakeBadge(_zoomText, HorizontalAlignment.Right);
        _zoomBadge.Visibility = Visibility.Collapsed;
        Children.Add(_badge);
        Children.Add(_zoomBadge);

        PointerWheelChanged += OnWheel;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += (_, e) => { _panStart = null; ReleasePointerCapture(e.Pointer); };
        DoubleTapped += (_, e) => { if (Annotations.Tool == DrawTool.None) ResetView(); e.Handled = true; };
        ManipulationMode = ManipulationModes.Scale | ManipulationModes.TranslateX | ManipulationModes.TranslateY;
        ManipulationDelta += OnManipulation;
        // While a drawing tool is active, touch strokes draw instead of pinching/panning.
        ManipulationStarting += (_, e) => { if (Annotations.Tool != DrawTool.None) e.Mode = ManipulationModes.None; };
    }

    private static Border MakeBadge(TextBlock text, HorizontalAlignment align) => new()
    {
        Child = text,
        Background = new SolidColorBrush(ColorHelper.FromArgb(150, 0, 0, 0)),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(8, 3, 8, 3),
        Margin = new Thickness(10),
        HorizontalAlignment = align,
        VerticalAlignment = VerticalAlignment.Top,
        IsHitTestVisible = false,
    };

    public AnnotationCanvas Annotations { get; } = new();

    public MediaPlayer? Player
    {
        get => _video.MediaPlayer;
        set => _video.SetMediaPlayer(value);
    }

    public int SourceWidth { get; private set; } = 1920;
    public int SourceHeight { get; private set; } = 1080;
    /// <summary>Clockwise camera mounting rotation (0/90/180/270).</summary>
    public int MountRotation { get; private set; }
    public float FineRotation { get; private set; }
    public bool Mirror { get; private set; }
    public bool Fill { get; set; }
    public float Zoom { get; private set; } = 1;
    public float MaxZoom { get; set; } = 8;
    public string? CameraId { get => Annotations.CameraId; set => Annotations.CameraId = value; }

    /// <summary>Raised when the user zooms with wheel/pinch (so live views can drive optical zoom).</summary>
    public event Action<VideoSurface, float>? ZoomRequested;

    /// <summary>Width/height as displayed (after mounting rotation).</summary>
    public float DisplayAspect => MountRotation % 180 == 0
        ? (float)SourceWidth / Math.Max(1, SourceHeight)
        : (float)SourceHeight / Math.Max(1, SourceWidth);

    public string Label
    {
        get => _badgeText.Text;
        set
        {
            _badgeText.Text = value;
            _badge.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public void SetSource(int width, int height, int rotation, float fineRotation, bool mirror)
    {
        SourceWidth = Math.Max(1, width);
        SourceHeight = Math.Max(1, height);
        MountRotation = ((rotation % 360) + 360) % 360;
        FineRotation = fineRotation;
        Mirror = mirror;
        Annotations.FrameAspect = (float)SourceWidth / SourceHeight;
        InvalidateArrange();
        (Parent as UIElement)?.InvalidateMeasure();
    }

    public void SetZoom(float zoom, Point? focus = null)
    {
        float old = Zoom;
        Zoom = Math.Clamp(zoom, 1, MaxZoom);
        if (focus is Point f && old > 0)
        {
            // Keep the point under the cursor fixed while zooming.
            double cx = f.X - ActualWidth / 2, cy = f.Y - ActualHeight / 2;
            double k = Zoom / old;
            _transform.TranslateX = cx - (cx - _transform.TranslateX) * k;
            _transform.TranslateY = cy - (cy - _transform.TranslateY) * k;
        }
        _zoomText.Text = $"{Zoom:0.0}×";
        _zoomBadge.Visibility = Zoom > 1.01f ? Visibility.Visible : Visibility.Collapsed;
        ApplyTransform();
    }

    public void ResetView()
    {
        _transform.TranslateX = _transform.TranslateY = 0;
        SetZoom(1);
        ZoomRequested?.Invoke(this, 1);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _clip.Rect = new Rect(0, 0, finalSize.Width, finalSize.Height);

        // The layer is sized to the source's own aspect; rotation happens in the transform.
        double dispW = MountRotation % 180 == 0 ? SourceWidth : SourceHeight;
        double dispH = MountRotation % 180 == 0 ? SourceHeight : SourceWidth;
        double sx = finalSize.Width / dispW, sy = finalSize.Height / dispH;
        double scale = Fill ? Math.Max(sx, sy) : Math.Min(sx, sy);
        double lw = SourceWidth * scale, lh = SourceHeight * scale;

        if (Math.Abs(_layer.Width - lw) > 0.5 || Math.Abs(_layer.Height - lh) > 0.5 || double.IsNaN(_layer.Width))
        {
            _layer.Width = lw;
            _layer.Height = lh;
        }
        _layer.Arrange(new Rect((finalSize.Width - lw) / 2, (finalSize.Height - lh) / 2, lw, lh));
        foreach (var child in Children)
            if (child != _layer) child.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
        ApplyTransform();
        return finalSize;
    }

    private void ApplyTransform()
    {
        float rotZoom = CoreLayout.RotationZoom(SourceWidth, SourceHeight, FineRotation);
        double s = rotZoom * Zoom;
        _transform.Rotation = MountRotation + FineRotation;
        _transform.ScaleX = Mirror ? -s : s;
        _transform.ScaleY = s;

        // Clamp panning so the picture always covers the tile when zoomed.
        double maxX = Math.Max(0, (_layer.Width * s - ActualWidth) / 2);
        double maxY = Math.Max(0, (_layer.Height * s - ActualHeight) / 2);
        if (MountRotation % 180 != 0)
        {
            maxX = Math.Max(0, (_layer.Height * s - ActualWidth) / 2);
            maxY = Math.Max(0, (_layer.Width * s - ActualHeight) / 2);
        }
        _transform.TranslateX = Math.Clamp(_transform.TranslateX, -maxX, maxX);
        _transform.TranslateY = Math.Clamp(_transform.TranslateY, -maxY, maxY);
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(this);
        float factor = pt.Properties.MouseWheelDelta > 0 ? 1.15f : 1 / 1.15f;
        SetZoom(Zoom * factor, pt.Position);
        ZoomRequested?.Invoke(this, Zoom);
        e.Handled = true;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(this);
        bool pan = pt.Properties.IsRightButtonPressed || pt.Properties.IsMiddleButtonPressed ||
                   (pt.Properties.IsLeftButtonPressed && Annotations.Tool == DrawTool.None && Zoom > 1);
        if (!pan) return;
        _panStart = pt.Position;
        _panOrigin = new Point(_transform.TranslateX, _transform.TranslateY);
        CapturePointer(e.Pointer);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_panStart is not Point start) return;
        var p = e.GetCurrentPoint(this).Position;
        _transform.TranslateX = _panOrigin.X + (p.X - start.X);
        _transform.TranslateY = _panOrigin.Y + (p.Y - start.Y);
        ApplyTransform();
    }

    private void OnManipulation(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        if (Annotations.Tool != DrawTool.None) return;
        if (Math.Abs(e.Delta.Scale - 1) > 0.001)
        {
            SetZoom(Zoom * e.Delta.Scale, e.Position);
            ZoomRequested?.Invoke(this, Zoom);
        }
        _transform.TranslateX += e.Delta.Translation.X;
        _transform.TranslateY += e.Delta.Translation.Y;
        ApplyTransform();
    }
}

/// <summary>
/// Lays out VideoSurfaces with the core's grid algorithm: the row/column
/// split that shows the most picture, with each tile at its camera's aspect,
/// or every tile at one uniform aspect (cropped) when UniformAspect is set.
/// </summary>
public sealed class VideoTilesPanel : Panel
{
    public float Gap { get; set; } = 8;

    private float _uniformAspect;
    public float UniformAspect
    {
        get => _uniformAspect;
        set
        {
            _uniformAspect = value;
            foreach (var s in Children.OfType<VideoSurface>()) s.Fill = value > 0;
            InvalidateArrange();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children) child.Measure(availableSize);
        double w = double.IsInfinity(availableSize.Width) ? 1280 : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height) ? 720 : availableSize.Height;
        return new Size(w, h);
    }

    /// <summary>Overlay every tile in the first tile's slot (ghost compare).</summary>
    public bool Stack { get; set; }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var surfaces = Children.OfType<VideoSurface>().Where(s => s.Visibility == Visibility.Visible).ToList();
        if (Stack && surfaces.Count > 0)
        {
            var (single, _, _, _) = CoreLayout.Grid((float)finalSize.Width, (float)finalSize.Height,
                [surfaces[0].DisplayAspect], 0, UniformAspect);
            var r = single.Length > 0 ? single[0] : new CoreLayout.Rect(0, 0, (float)finalSize.Width, (float)finalSize.Height);
            foreach (var s in surfaces) s.Arrange(new Rect(r.X, r.Y, r.W, r.H));
            return finalSize;
        }
        var aspects = surfaces.Select(s => s.DisplayAspect).ToList();
        var (content, _, _, _) = CoreLayout.Grid((float)finalSize.Width, (float)finalSize.Height, aspects, Gap, UniformAspect);
        for (int i = 0; i < surfaces.Count; i++)
        {
            if (i < content.Length)
            {
                var r = content[i];
                surfaces[i].Arrange(new Rect(r.X, r.Y, Math.Max(0, r.W), Math.Max(0, r.H)));
            }
            else
            {
                surfaces[i].Arrange(new Rect(0, 0, 0, 0));
            }
        }
        return finalSize;
    }
}
