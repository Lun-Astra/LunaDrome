using Microsoft.UI.Xaml.Data;

namespace LunaDrome.Controls;

/// <summary>
/// Stable accent color per genre name for the Feishin-style genre tiles' left bar: the name is
/// hashed to a hue, so "Grunge" is always the same color, with Feishin-like saturation/lightness.
/// </summary>
public sealed class GenreColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var name = value as string ?? "";
        uint hash = 2166136261;
        foreach (var c in name.ToLowerInvariant())
            hash = (hash ^ c) * 16777619;

        var hue = hash % 360;
        return new SolidColorBrush(FromHsl(hue, 0.62, 0.58));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();

    private static Windows.UI.Color FromHsl(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        return Windows.UI.Color.FromArgb(255, (byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
