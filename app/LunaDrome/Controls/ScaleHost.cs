using Windows.Foundation;

namespace LunaDrome.Controls;

/// <summary>
/// Zooms its single child down proportionally when the window is smaller than the design size,
/// so a non-maximized window looks like the full-screen layout, just smaller (Luna's choice
/// over reflowing/responsive layout). The child is laid out at the full design-scale size
/// (available / scale) and then shrunk with a ScaleTransform - WinUI has no LayoutTransform,
/// and a plain RenderTransform alone would leave layout at the unscaled size.
///
/// Scale = min(width / DesignWidth, height / DesignHeight), clamped to [MinScale, 1]: never
/// enlarges past the full-screen look, and stops shrinking at MinScale so text stays readable
/// (below that, the layout just gets less room, as before).
/// </summary>
public sealed class ScaleHost : Panel
{
    // Full size (scale 1) from 1600x850 up; Luna found 1920x1010 / 75% too small in windowed mode.
    public double DesignWidth { get; set; } = 1600;
    public double DesignHeight { get; set; } = 850;
    public double MinScale { get; set; } = 0.85;

    private readonly ScaleTransform _transform = new();

    public double Scale { get; private set; } = 1;

    private double ComputeScale(Size size)
    {
        if (double.IsInfinity(size.Width) || double.IsInfinity(size.Height) || size.Width <= 0 || size.Height <= 0)
            return 1;

        var scale = Math.Min(size.Width / DesignWidth, size.Height / DesignHeight);
        return Math.Clamp(scale, MinScale, 1);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var scale = ComputeScale(availableSize);
        foreach (var child in Children)
            child.Measure(new Size(availableSize.Width / scale, availableSize.Height / scale));

        return new Size(
            double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Scale = ComputeScale(finalSize);
        _transform.ScaleX = Scale;
        _transform.ScaleY = Scale;

        foreach (var child in Children)
        {
            child.RenderTransformOrigin = new Windows.Foundation.Point(0, 0);
            child.RenderTransform = _transform;
            child.Arrange(new Rect(0, 0, finalSize.Width / Scale, finalSize.Height / Scale));
        }

        return finalSize;
    }
}
