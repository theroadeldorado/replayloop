using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SwingLoop.Interop;
using SwingLoop.Models;
using Windows.Foundation;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace SwingLoop.Controls;

public enum DrawTool { None, Select, Line, Arrow, Circle, Rectangle, Angle, Freehand }

/// <summary>
/// Vector drawing layer laid exactly over the video frame. Shapes are stored
/// normalized (0..1) to the frame, so they stay put at any zoom or window size
/// and remain editable. Two layers: persistent shapes (shown on every shot
/// from this camera, and on the live view) and shapes for the current shot.
/// </summary>
public sealed class AnnotationCanvas : Canvas
{
    private const double HandleRadius = 7;
    private const float HitTolerance = 0.02f;

    private List<ShapeDto> _shot = [];
    private List<ShapeDto> _persistent = [];
    private readonly Stack<(List<ShapeDto> Shot, List<ShapeDto> Persistent)> _undo = new();

    private ShapeDto? _drawing;
    private ShapeDto? _selected;
    private int _dragHandle = -1;  // point index, or -1 to move the whole shape
    private Point _dragStart;
    private List<float[]>? _dragOriginal;
    private bool _angleSecondArm;
    private DrawTool _tool;

    public AnnotationCanvas()
    {
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerCanceled += (_, _) => CancelGesture();
        SizeChanged += (_, _) => Redraw();
        IsHitTestVisible = false;
    }

    public DrawTool Tool
    {
        get => _tool;
        set
        {
            if (_angleSecondArm) CommitDrawing();
            _tool = value;
            IsHitTestVisible = value != DrawTool.None;
            Background = value == DrawTool.None ? null : new SolidColorBrush(Colors.Transparent);
            if (value != DrawTool.Select) _selected = null;
            ProtectedCursor = InputSystemCursor.Create(value == DrawTool.Select ? InputSystemCursorShape.Arrow : InputSystemCursorShape.Cross);
            Redraw();
        }
    }

    public string Color { get; set; } = "#FF3B30FF";
    public float StrokeWidth { get; set; } = 4;
    public bool DrawPersistent { get; set; }
    public bool Dashed { get; set; }
    public string? CameraId { get; set; }
    public bool ShowPersistent { get; set; } = true;
    public bool ShowShot { get; set; } = true;
    public float FrameAspect { get; set; } = 16f / 9f;

    public IReadOnlyList<ShapeDto> ShotShapes => _shot;
    public IReadOnlyList<ShapeDto> PersistentShapes => _persistent;
    public bool HasSelection => _selected is not null;
    public bool CanUndo => _undo.Count > 0;

    /// <summary>Raised after an edit. The bool is true when persistent shapes changed.</summary>
    public event Action<AnnotationCanvas, bool>? ShapesChanged;

    /// <summary>
    /// Loads the shapes for this surface's camera. Shapes carry the camera they
    /// were drawn on, so a line on the face-on view never shows on down-the-line.
    /// </summary>
    public void SetShapes(IEnumerable<ShapeDto> shot, IEnumerable<ShapeDto> persistent)
    {
        _shot = shot.Where(BelongsHere).Select(s => s.Clone()).ToList();
        _persistent = persistent.Where(BelongsHere).Select(s => s.Clone()).ToList();
        _selected = null;
        _drawing = null;
        _undo.Clear();
        Redraw();
    }

    private bool BelongsHere(ShapeDto s) => s.CameraId is null || CameraId is null || s.CameraId == CameraId;

    /// <summary>Replace this camera's shapes inside a list holding every camera's shapes.</summary>
    public List<ShapeDto> MergeInto(IEnumerable<ShapeDto> all, bool persistent) =>
        all.Where(s => !BelongsHere(s)).Concat((persistent ? _persistent : _shot).Select(s => s.Clone())).ToList();

    public void ApplyColorToSelection(string color)
    {
        if (_selected is null) return;
        Snapshot();
        _selected.Color = color;
        Changed(_selected.Persistent);
    }

    public void ApplyStrokeToSelection(float width)
    {
        if (_selected is null) return;
        Snapshot();
        _selected.StrokeWidth = width;
        Changed(_selected.Persistent);
    }

    public void DeleteSelected()
    {
        if (_selected is null) return;
        Snapshot();
        bool persistent = _selected.Persistent;
        (persistent ? _persistent : _shot).Remove(_selected);
        _selected = null;
        Changed(persistent);
    }

    /// <summary>Move the selected shape between "this shot" and "every shot".</summary>
    public void ToggleSelectedPersistence()
    {
        if (_selected is null) return;
        Snapshot();
        var s = _selected;
        (s.Persistent ? _persistent : _shot).Remove(s);
        s.Persistent = !s.Persistent;
        (s.Persistent ? _persistent : _shot).Add(s);
        ShapesChanged?.Invoke(this, true);
        ShapesChanged?.Invoke(this, false);
        Redraw();
    }

    public void ClearShot()
    {
        if (_shot.Count == 0) return;
        Snapshot();
        _shot.Clear();
        _selected = null;
        Changed(false);
    }

    public void ClearPersistent()
    {
        if (_persistent.Count == 0) return;
        Snapshot();
        _persistent.Clear();
        _selected = null;
        Changed(true);
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        (_shot, _persistent) = _undo.Pop();
        _selected = null;
        ShapesChanged?.Invoke(this, true);
        ShapesChanged?.Invoke(this, false);
        Redraw();
    }

    private void Snapshot()
    {
        _undo.Push((_shot.Select(s => s.Clone()).ToList(), _persistent.Select(s => s.Clone()).ToList()));
        if (_undo.Count > 100)
        {
            var keep = _undo.Take(100).Reverse().ToList();
            _undo.Clear();
            foreach (var k in keep) _undo.Push(k);
        }
    }

    private void Changed(bool persistent)
    {
        ShapesChanged?.Invoke(this, persistent);
        Redraw();
    }

    // ---------------------------------------------------------------- input

    private float[] Normalize(Point p) =>
        [(float)Math.Clamp(p.X / Math.Max(1, ActualWidth), 0, 1), (float)Math.Clamp(p.Y / Math.Max(1, ActualHeight), 0, 1)];

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(this);
        if (!pt.Properties.IsLeftButtonPressed && pt.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse) return;
        var p = Normalize(pt.Position);
        CapturePointer(e.Pointer);
        e.Handled = true;

        if (_tool == DrawTool.Select)
        {
            BeginSelectDrag(pt.Position, p);
            return;
        }
        if (_tool == DrawTool.Angle && _angleSecondArm && _drawing is not null)
        {
            _drawing.Points[2] = p;
            CommitDrawing();
            return;
        }

        var kind = _tool switch
        {
            DrawTool.Arrow => ShapeKind.Arrow,
            DrawTool.Circle => ShapeKind.Circle,
            DrawTool.Rectangle => ShapeKind.Rectangle,
            DrawTool.Angle => ShapeKind.Angle,
            DrawTool.Freehand => ShapeKind.Freehand,
            _ => ShapeKind.Line,
        };
        _drawing = new ShapeDto
        {
            Id = CoreIds.NewId(),
            Kind = kind,
            // Angle: [arm 1 end, vertex, arm 2 end]; the drag starts at the vertex.
            Points = kind == ShapeKind.Angle ? [(float[])p.Clone(), p, (float[])p.Clone()] : [p, (float[])p.Clone()],
            Color = Color,
            StrokeWidth = StrokeWidth,
            Dashed = Dashed,
            Persistent = DrawPersistent,
            CameraId = CameraId,
        };
        Redraw();
    }

    private void BeginSelectDrag(Point pos, float[] p)
    {
        // Handles of the current selection win over shape bodies.
        if (_selected is not null && _selected.Kind != ShapeKind.Freehand)
        {
            for (int i = 0; i < _selected.Points.Count; i++)
            {
                var hp = ToPixels(_selected.Points[i]);
                if (Math.Abs(hp.X - pos.X) <= HandleRadius * 1.6 && Math.Abs(hp.Y - pos.Y) <= HandleRadius * 1.6)
                {
                    StartDrag(i, pos);
                    return;
                }
            }
        }
        var visible = VisibleShapes().ToList();
        int hit = visible.Count == 0 ? -1 : Native.sc_annotation_hit_test(Json.Serialize(visible), p[0], p[1], HitTolerance, FrameAspect);
        _selected = hit >= 0 ? visible[hit] : null;
        if (_selected is not null) StartDrag(-1, pos);
        Redraw();
    }

    private void StartDrag(int handle, Point pos)
    {
        Snapshot();
        _dragHandle = handle;
        _dragStart = pos;
        _dragOriginal = _selected!.Points.Select(q => (float[])q.Clone()).ToList();
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(this).Position;
        var p = Normalize(pos);

        if (_tool == DrawTool.Select && _selected is not null && _dragOriginal is not null)
        {
            if (_dragHandle >= 0)
            {
                _selected.Points[_dragHandle] = p;
            }
            else
            {
                float dx = (float)((pos.X - _dragStart.X) / ActualWidth), dy = (float)((pos.Y - _dragStart.Y) / ActualHeight);
                for (int i = 0; i < _selected.Points.Count; i++)
                    _selected.Points[i] = [_dragOriginal[i][0] + dx, _dragOriginal[i][1] + dy];
            }
            Redraw();
            return;
        }

        if (_drawing is null) return;
        switch (_drawing.Kind)
        {
            case ShapeKind.Freehand:
                var last = ToPixels(_drawing.Points[^1]);
                if (Math.Abs(last.X - pos.X) + Math.Abs(last.Y - pos.Y) >= 2) _drawing.Points.Add(p);
                break;
            case ShapeKind.Angle:
                _drawing.Points[_angleSecondArm ? 2 : 0] = p;
                break;
            default:
                _drawing.Points[^1] = p;
                break;
        }
        Redraw();
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        ReleasePointerCapture(e.Pointer);
        if (_tool == DrawTool.Select)
        {
            if (_dragOriginal is not null && _selected is not null) Changed(_selected.Persistent);
            _dragOriginal = null;
            _dragHandle = -1;
            return;
        }
        if (_drawing is null) return;
        if (_drawing.Kind == ShapeKind.Angle && !_angleSecondArm)
        {
            _angleSecondArm = true;  // now move to aim the second arm, click to finish
            return;
        }
        CommitDrawing();
    }

    private void CommitDrawing()
    {
        var d = _drawing;
        _drawing = null;
        _angleSecondArm = false;
        if (d is null) return;
        var a = ToPixels(d.Points[0]);
        var b = ToPixels(d.Points[^1]);
        bool tiny = d.Kind != ShapeKind.Freehand && d.Kind != ShapeKind.Angle && Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) < 4;
        if (!tiny)
        {
            Snapshot();
            (d.Persistent ? _persistent : _shot).Add(d);
            Changed(d.Persistent);
        }
        else
        {
            Redraw();
        }
    }

    private void CancelGesture()
    {
        _drawing = null;
        _angleSecondArm = false;
        _dragOriginal = null;
        Redraw();
    }

    // ---------------------------------------------------------------- render

    private IEnumerable<ShapeDto> VisibleShapes()
    {
        if (ShowPersistent) foreach (var s in _persistent) yield return s;
        if (ShowShot) foreach (var s in _shot) yield return s;
    }

    private Point ToPixels(float[] p) => new(p[0] * ActualWidth, p[1] * ActualHeight);

    public void Redraw()
    {
        Children.Clear();
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        foreach (var s in VisibleShapes()) Draw(s, s == _selected);
        if (_drawing is not null) Draw(_drawing, false);
        if (_selected is not null && _tool == DrawTool.Select) DrawHandles(_selected);
    }

    public static Windows.UI.Color ParseColor(string hex)
    {
        if (hex.Length is 7 or 9 && hex[0] == '#' &&
            uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v))
        {
            return hex.Length == 7
                ? Microsoft.UI.ColorHelper.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v)
                : Microsoft.UI.ColorHelper.FromArgb((byte)v, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8));
        }
        return Colors.Red;
    }

    private void Draw(ShapeDto s, bool selected)
    {
        if (s.Points.Count == 0) return;
        var brush = new SolidColorBrush(ParseColor(s.Color));
        double width = Math.Max(1.5, s.StrokeWidth * ActualHeight / 1080.0);
        var pts = s.Points.Select(ToPixels).ToList();

        Shape shape;
        switch (s.Kind)
        {
            case ShapeKind.Circle:
            {
                double r = pts.Count > 1 ? Distance(pts[0], pts[1]) : 0;
                shape = new Ellipse { Width = r * 2, Height = r * 2 };
                SetLeft(shape, pts[0].X - r);
                SetTop(shape, pts[0].Y - r);
                break;
            }
            case ShapeKind.Rectangle:
            {
                var b = pts.Count > 1 ? pts[1] : pts[0];
                shape = new Rectangle { Width = Math.Abs(b.X - pts[0].X), Height = Math.Abs(b.Y - pts[0].Y) };
                SetLeft(shape, Math.Min(pts[0].X, b.X));
                SetTop(shape, Math.Min(pts[0].Y, b.Y));
                break;
            }
            case ShapeKind.Arrow:
                shape = new XamlPath { Data = ArrowGeometry(pts[0], pts[^1], width) };
                break;
            case ShapeKind.Line:
                shape = new Line { X1 = pts[0].X, Y1 = pts[0].Y, X2 = pts[^1].X, Y2 = pts[^1].Y };
                break;
            default:
            {
                var poly = new Polyline();
                foreach (var p in pts) poly.Points.Add(p);
                shape = poly;
                break;
            }
        }
        shape.Stroke = brush;
        shape.StrokeThickness = selected ? width + 2 : width;
        shape.StrokeStartLineCap = shape.StrokeEndLineCap = PenLineCap.Round;
        shape.StrokeLineJoin = PenLineJoin.Round;
        if (s.Dashed) shape.StrokeDashArray = [3, 2];
        shape.IsHitTestVisible = false;
        Children.Add(shape);

        if (s.Kind == ShapeKind.Angle && pts.Count == 3)
        {
            float deg = Native.sc_annotation_angle(Json.Serialize(s), FrameAspect);
            AddLabel($"{deg:0}°", pts[1], brush);
        }
    }

    private void AddLabel(string text, Point at, Brush brush)
    {
        var label = new Border
        {
            Background = new SolidColorBrush(ColorHelper.FromArgb(170, 0, 0, 0)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 2, 6, 2),
            Child = new TextBlock { Text = text, Foreground = brush, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
            IsHitTestVisible = false,
        };
        SetLeft(label, at.X + 10);
        SetTop(label, at.Y + 6);
        Children.Add(label);
    }

    private void DrawHandles(ShapeDto s)
    {
        if (s.Kind == ShapeKind.Freehand) return;
        foreach (var p in s.Points.Select(ToPixels))
        {
            var h = new Ellipse
            {
                Width = HandleRadius * 2,
                Height = HandleRadius * 2,
                Fill = new SolidColorBrush(Colors.White),
                Stroke = new SolidColorBrush(ColorHelper.FromArgb(255, 10, 132, 255)),
                StrokeThickness = 2,
                IsHitTestVisible = false,
            };
            SetLeft(h, p.X - HandleRadius);
            SetTop(h, p.Y - HandleRadius);
            Children.Add(h);
        }
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static Geometry ArrowGeometry(Point from, Point to, double width)
    {
        double angle = Math.Atan2(to.Y - from.Y, to.X - from.X);
        double head = Math.Max(10, width * 4);
        Point Wing(double a) => new(to.X - head * Math.Cos(angle + a), to.Y - head * Math.Sin(angle + a));

        var figure = new PathFigure { StartPoint = from, IsClosed = false };
        figure.Segments.Add(new LineSegment { Point = to });
        var headFigure = new PathFigure { StartPoint = Wing(0.45), IsClosed = false };
        headFigure.Segments.Add(new LineSegment { Point = to });
        headFigure.Segments.Add(new LineSegment { Point = Wing(-0.45) });
        var g = new PathGeometry();
        g.Figures.Add(figure);
        g.Figures.Add(headFigure);
        return g;
    }
}
