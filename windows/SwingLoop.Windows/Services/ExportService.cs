using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using SwingLoop.Controls;
using SwingLoop.Interop;
using SwingLoop.Models;
using Windows.Foundation;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace SwingLoop.Services;

/// <summary>
/// Saves and shares shots, with or without drawings. Drawings are burned in
/// only on export; the session keeps them as editable vectors.
/// </summary>
public static class ExportService
{
    public static string ExportFolder
    {
        get
        {
            string dir = Path.Combine(Path.GetTempPath(), "SwingLoop-export");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static async Task ExportClipAsync(string clipPath, ClipDto clip, IReadOnlyList<ShapeDto> shapes,
        bool withDrawings, string outPath)
    {
        if (!withDrawings || shapes.Count == 0)
        {
            File.Copy(clipPath, outPath, overwrite: true);
            return;
        }

        string overlayPng = Path.Combine(ExportFolder, $"overlay-{Guid.NewGuid():N}.png");
        try
        {
            await RenderOverlayAsync(shapes, clip.Width, clip.Height, overlayPng);

            var composition = new MediaComposition();
            var source = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(clipPath));
            composition.Clips.Add(source);

            var overlayClip = await MediaClip.CreateFromImageFileAsync(
                await StorageFile.GetFileFromPathAsync(overlayPng), source.OriginalDuration);
            var layer = new MediaOverlayLayer();
            layer.Overlays.Add(new MediaOverlay(overlayClip)
            {
                Position = new Rect(0, 0, clip.Width, clip.Height),
                Opacity = 1.0,
            });
            composition.OverlayLayers.Add(layer);

            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
            profile.Audio = null;
            profile.Video.Width = (uint)clip.Width;
            profile.Video.Height = (uint)clip.Height;

            using (File.Create(outPath)) { }
            var outFile = await StorageFile.GetFileFromPathAsync(outPath);
            var result = await composition.RenderToFileAsync(outFile, MediaTrimmingPreference.Precise, profile);
            if (result != TranscodeFailureReason.None)
                throw new InvalidOperationException($"Export failed: {result}");
        }
        finally
        {
            TryDelete(overlayPng);
        }
    }

    /// <summary>One frame as PNG, e.g. the impact position, optionally with drawings.</summary>
    public static async Task ExportStillAsync(string clipPath, ClipDto clip, TimeSpan position,
        IReadOnlyList<ShapeDto> shapes, bool withDrawings, string outPath)
    {
        var composition = new MediaComposition();
        composition.Clips.Add(await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(clipPath)));
        using var frameStream = await composition.GetThumbnailAsync(position, clip.Width, clip.Height,
            VideoFramePrecision.NearestFrame);

        var device = CanvasDevice.GetSharedDevice();
        using var frame = await CanvasBitmap.LoadAsync(device, frameStream);
        using var target = new CanvasRenderTarget(device, clip.Width, clip.Height, 96);
        using (var ds = target.CreateDrawingSession())
        {
            ds.DrawImage(frame, new Rect(0, 0, clip.Width, clip.Height));
            if (withDrawings) DrawShapes(ds, shapes, clip.Width, clip.Height);
        }
        await target.SaveAsync(outPath, CanvasBitmapFileFormat.Png);
    }

    public static async Task RenderOverlayAsync(IReadOnlyList<ShapeDto> shapes, int width, int height, string pngPath)
    {
        var device = CanvasDevice.GetSharedDevice();
        using var target = new CanvasRenderTarget(device, width, height, 96);
        using (var ds = target.CreateDrawingSession())
        {
            ds.Clear(Colors.Transparent);
            DrawShapes(ds, shapes, width, height);
        }
        await target.SaveAsync(pngPath, CanvasBitmapFileFormat.Png);
    }

    /// <summary>Same geometry as AnnotationCanvas, rendered with Win2D at full video resolution.</summary>
    private static void DrawShapes(CanvasDrawingSession ds, IReadOnlyList<ShapeDto> shapes, int w, int h)
    {
        float aspect = (float)w / h;
        using var style = new CanvasStrokeStyle
        {
            StartCap = CanvasCapStyle.Round,
            EndCap = CanvasCapStyle.Round,
            LineJoin = CanvasLineJoin.Round,
        };
        using var dashed = new CanvasStrokeStyle
        {
            StartCap = CanvasCapStyle.Round,
            EndCap = CanvasCapStyle.Round,
            DashStyle = CanvasDashStyle.Dash,
        };

        foreach (var s in shapes)
        {
            if (s.Points.Count == 0) continue;
            var color = AnnotationCanvas.ParseColor(s.Color);
            float width = Math.Max(1.5f, s.StrokeWidth * h / 1080f);
            var pts = s.Points.Select(p => new Vector2(p[0] * w, p[1] * h)).ToList();
            var st = s.Dashed ? dashed : style;

            switch (s.Kind)
            {
                case ShapeKind.Circle:
                    ds.DrawCircle(pts[0], pts.Count > 1 ? Vector2.Distance(pts[0], pts[1]) : 0, color, width, st);
                    break;
                case ShapeKind.Rectangle:
                {
                    var b = pts[^1];
                    ds.DrawRectangle(Math.Min(pts[0].X, b.X), Math.Min(pts[0].Y, b.Y),
                        Math.Abs(b.X - pts[0].X), Math.Abs(b.Y - pts[0].Y), color, width, st);
                    break;
                }
                case ShapeKind.Line:
                    ds.DrawLine(pts[0], pts[^1], color, width, st);
                    break;
                case ShapeKind.Arrow:
                {
                    var from = pts[0];
                    var to = pts[^1];
                    ds.DrawLine(from, to, color, width, st);
                    float angle = MathF.Atan2(to.Y - from.Y, to.X - from.X);
                    float head = Math.Max(10, width * 4);
                    Vector2 Wing(float a) => new(to.X - head * MathF.Cos(angle + a), to.Y - head * MathF.Sin(angle + a));
                    ds.DrawLine(Wing(0.45f), to, color, width, st);
                    ds.DrawLine(Wing(-0.45f), to, color, width, st);
                    break;
                }
                default:
                {
                    using var builder = new CanvasPathBuilder(ds);
                    builder.BeginFigure(pts[0]);
                    foreach (var p in pts.Skip(1)) builder.AddLine(p);
                    builder.EndFigure(CanvasFigureLoop.Open);
                    using var geometry = CanvasGeometry.CreatePath(builder);
                    ds.DrawGeometry(geometry, color, width, st);
                    if (s.Kind == ShapeKind.Angle && pts.Count == 3)
                    {
                        float deg = Native.sc_annotation_angle(Json.Serialize(s), aspect);
                        using var format = new CanvasTextFormat { FontSize = h / 30f, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
                        ds.DrawText($"{deg:0}°", pts[1] + new Vector2(h / 80f, h / 120f), color, format);
                    }
                    break;
                }
            }
        }
    }

    public static string SuggestedName(ShotDto shot, ClipDto clip, bool withDrawings, string ext)
    {
        string date = DateTime.TryParse(shot.CreatedAt, out var t) ? t.ToLocalTime().ToString("yyyy-MM-dd HHmm") : "shot";
        string cam = Path.GetFileNameWithoutExtension(clip.File);
        return $"SwingLoop {date} #{shot.Number} {cam}{(withDrawings ? " marked" : "")}{ext}";
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
