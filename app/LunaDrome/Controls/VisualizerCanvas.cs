using SkiaSharp;
using Uno.WinUI.Graphics2DSK;

namespace LunaDrome.Controls;

/// <summary>
/// Draws the visualizer curve straight onto the window with SkiaSharp. The first Uno port
/// rebuilt a XAML PathGeometry (~130 Bezier segments) and resized the Path every frame, which
/// forced Uno to re-measure/re-layout and re-convert the geometry each frame - that's what made
/// it slow and low-fps. Here a frame is just: build one SKPath, fill + stroke it. No layout.
///
/// Curve shape is the WPF app's, unchanged (see MainWindow.xaml.cs OnVisualizerRenderFrame /
/// UpdateSmoothCurve and PROJECT_MEMORY.md): 5-tap spatial average, smoothstep fade over 22
/// bands at each end, 38px flat anchors, mid-height baseline, Catmull-Rom-to-Bezier with
/// horizontal-tangent connectors, control points clamped between the top padding and baseline.
/// </summary>
public sealed class VisualizerCanvas : SKCanvasElement
{
    private const float Padding = 6;
    private const float AnchorWidth = 38;
    private const int FadeBands = 22;

    private float[] _levels = Array.Empty<float>();
    private SKPoint[] _points = Array.Empty<SKPoint>();
    private readonly SKPath _path = new();

    private readonly SKPaint _stroke = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 2,
        StrokeJoin = SKStrokeJoin.Round,
        StrokeCap = SKStrokeCap.Round,
        Color = SKColors.White
    };

    private readonly SKPaint _fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private float _fillShaderHeight = -1;
    private SKColor _fillColor = SKColors.White;

    /// <summary>Hand over this frame's (temporally smoothed) band levels, 0..1, and redraw.</summary>
    public void SetLevels(float[] smoothedBands)
    {
        if (_levels.Length != smoothedBands.Length)
        {
            _levels = new float[smoothedBands.Length];
            _points = new SKPoint[smoothedBands.Length + 4];
        }

        // 5-tap spatial average - rounds off sharp band-to-band jumps anywhere along the curve.
        var n = smoothedBands.Length;
        for (var i = 0; i < n; i++)
        {
            float sum = 0;
            var count = 0;
            for (var k = -2; k <= 2; k++)
            {
                var idx = i + k;
                if (idx < 0 || idx >= n)
                    continue;
                sum += smoothedBands[idx];
                count++;
            }
            _levels[i] = sum / count;
        }

        Invalidate();
    }

    protected override void RenderOverride(SKCanvas canvas, Windows.Foundation.Size area)
    {
        var width = (float)area.Width;
        var height = (float)area.Height;
        var bandCount = _levels.Length;
        if (width <= 0 || height <= 0 || bandCount < 2)
            return;

        var baselineY = height / 2;
        var drawableHeight = Math.Max(1, baselineY - Padding);
        var moveZoneWidth = Math.Max(1, width - Padding * 2 - AnchorWidth * 2);
        var leftMoveX = Padding + AnchorWidth;
        var rightMoveX = width - Padding - AnchorWidth;

        var points = _points;
        points[0] = new SKPoint(Padding, baselineY);
        points[1] = new SKPoint(leftMoveX, baselineY);
        for (var i = 0; i < bandCount; i++)
        {
            float fade = 1f;
            if (i < FadeBands)
            {
                var t = (i + 1f) / (FadeBands + 1f);
                fade = t * t * (3f - 2f * t);
            }
            else if (i >= bandCount - FadeBands)
            {
                var t = (bandCount - i) / (float)(FadeBands + 1);
                fade = t * t * (3f - 2f * t);
            }

            var x = leftMoveX + i * moveZoneWidth / (bandCount - 1);
            points[i + 2] = new SKPoint(x, baselineY - _levels[i] * fade * drawableHeight);
        }
        points[^2] = new SKPoint(rightMoveX, baselineY);
        points[^1] = new SKPoint(width - Padding, baselineY);

        BuildPath(points, bandCount, Padding, baselineY);

        // Filled area under the curve: white 40% at the top fading to transparent at the bottom
        // (same gradient as the WPF Path.Fill), then the 2px white outline on top.
        // #23: drawn in the accent color picked in Settings (white for the neutral accent).
        var accent = Themes.CurrentAccent;
        var color = new SKColor(accent.R, accent.G, accent.B);
        if (Math.Abs(_fillShaderHeight - height) > 0.5f || color != _fillColor)
        {
            _fill.Shader?.Dispose();
            _fill.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(0, height),
                new[] { color.WithAlpha(0x66), color.WithAlpha(0) },
                SKShaderTileMode.Clamp);
            _fillShaderHeight = height;
            _fillColor = color;
            _stroke.Color = color;
        }

        canvas.DrawPath(_path, _fill);
        canvas.DrawPath(_path, _stroke);
    }

    private void BuildPath(SKPoint[] points, int bandCount, float minY, float maxY)
    {
        _path.Reset();
        _path.MoveTo(points[0]);
        var segmentCount = points.Length - 1;

        for (var i = 0; i < segmentCount; i++)
        {
            var p1 = points[i];
            var p2 = points[i + 1];

            // Flat anchor zones: straight lines, guaranteed not to curve.
            if (i == 0 || i == segmentCount - 1)
            {
                _path.LineTo(p2);
                continue;
            }

            SKPoint c1, c2;
            if (i == 1)
            {
                // Entry connector: leaves the flat zone dead horizontal.
                var next = points[i + 2];
                c1 = new SKPoint(p1.X + (p2.X - p1.X) / 3f, p1.Y);
                c2 = new SKPoint(p2.X - (next.X - p1.X) / 6f, Math.Clamp(p2.Y - (next.Y - p1.Y) / 6f, minY, maxY));
            }
            else if (i == bandCount + 1)
            {
                // Exit connector: arrives at the flat zone dead horizontal.
                var prev = points[i - 1];
                c1 = new SKPoint(p1.X + (p2.X - prev.X) / 6f, Math.Clamp(p1.Y + (p2.Y - prev.Y) / 6f, minY, maxY));
                c2 = new SKPoint(p2.X - (p2.X - p1.X) / 3f, p2.Y);
            }
            else
            {
                // Interior: standard Catmull-Rom through the real neighboring points.
                var p0 = points[i - 1];
                var p3 = points[i + 2];
                c1 = new SKPoint(p1.X + (p2.X - p0.X) / 6f, Math.Clamp(p1.Y + (p2.Y - p0.Y) / 6f, minY, maxY));
                c2 = new SKPoint(p2.X - (p3.X - p1.X) / 6f, Math.Clamp(p2.Y - (p3.Y - p1.Y) / 6f, minY, maxY));
            }

            _path.CubicTo(c1, c2, p2);
        }

        // Left open on purpose: Skia fills an open contour as if closed (both ends sit on the
        // baseline, so the implied edge is exactly the flat bottom of the filled area), while
        // the stroke doesn't draw that closing edge - same as WPF's IsFilled without IsClosed.
    }
}
