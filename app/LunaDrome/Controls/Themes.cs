using Windows.UI;

namespace LunaDrome.Controls;

/// <summary>
/// Themes + accent colors (Settings page, #15). Every color in App.xaml is a named
/// SolidColorBrush used through {StaticResource}, i.e. the same brush instance everywhere - so a
/// theme just recolors those brushes in place and the whole UI follows live, no restart.
/// Each brush's role comes from its original (Dark theme) color.
/// </summary>
public static class Themes
{
    public static readonly string[] ThemeNames = { "Dark", "Midnight", "Black", "Nord", "Mocha", "Forest", "Wine", "Light", "Creme", "Sakura" };

    /// <summary>Light background (the Settings page lists these on their own row).</summary>
    public static bool IsLight(string theme) => PaletteFor(theme).Background.R > 128;
    public static readonly string[] AccentNames = { "White", "Blue", "Violet", "Green", "Pink", "Orange" };

    private enum Role { Background, Panel, Raised, Hover, Border, Accent, AccentDim, OnAccent, TextPrimary, TextSecondary }

    private sealed record Palette(Color Background, Color Panel, Color Raised, Color Hover, Color Border,
                                  Color AccentDim, Color TextPrimary, Color TextSecondary, Color NeutralAccent, Color OnNeutralAccent);

    private static Color C(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private static readonly Dictionary<string, Palette> Palettes = new()
    {
        ["Dark"] = new(C(0xFF1A1B1E), C(0xFF141517), C(0xFF25262B), C(0xFF2C2E33), C(0xFF373A40),
                       C(0x26FFFFFF), C(0xFFECEDEE), C(0xFF909296), C(0xFFFFFFFF), C(0xFF141517)),
        ["Midnight"] = new(C(0xFF11151F), C(0xFF0C0F17), C(0xFF1B2130), C(0xFF232A3B), C(0xFF2E3648),
                           C(0x26FFFFFF), C(0xFFE6EAF2), C(0xFF8B93A7), C(0xFFFFFFFF), C(0xFF0C0F17)),
        ["Black"] = new(C(0xFF000000), C(0xFF000000), C(0xFF141414), C(0xFF1E1E1E), C(0xFF2A2A2A),
                        C(0x26FFFFFF), C(0xFFEDEDED), C(0xFF8A8A8A), C(0xFFFFFFFF), C(0xFF000000)),
        ["Nord"] = new(C(0xFF2E3440), C(0xFF272C36), C(0xFF3B4252), C(0xFF434C5E), C(0xFF4C566A),
                       C(0x26FFFFFF), C(0xFFECEFF4), C(0xFF9AA3B5), C(0xFFECEFF4), C(0xFF272C36)),
        ["Mocha"] = new(C(0xFF231D1A), C(0xFF1C1714), C(0xFF2F2723), C(0xFF38302A), C(0xFF463B34),
                        C(0x26FFFFFF), C(0xFFF0E6DC), C(0xFFA3968A), C(0xFFF0E6DC), C(0xFF1C1714)),
        ["Forest"] = new(C(0xFF17201B), C(0xFF111814), C(0xFF212C26), C(0xFF29362E), C(0xFF34443A),
                         C(0x26FFFFFF), C(0xFFE6EFE9), C(0xFF8FA396), C(0xFFE6EFE9), C(0xFF111814)),
        ["Wine"] = new(C(0xFF21161A), C(0xFF1A1114), C(0xFF2E1F25), C(0xFF37262D), C(0xFF473139),
                       C(0x26FFFFFF), C(0xFFF2E6EA), C(0xFFA68F97), C(0xFFF2E6EA), C(0xFF1A1114)),
        ["Light"] = new(C(0xFFF3F4F6), C(0xFFFFFFFF), C(0xFFE6E8EC), C(0xFFDCDFE4), C(0xFFD0D4DA),
                        C(0x1A000000), C(0xFF1A1B1E), C(0xFF5F636B), C(0xFF1A1B1E), C(0xFFFFFFFF)),
        ["Creme"] = new(C(0xFFF5EFE3), C(0xFFFBF7EF), C(0xFFEAE2D2), C(0xFFE2D8C5), C(0xFFD6CAB3),
                        C(0x1A3B2E22), C(0xFF2E2419), C(0xFF7A6A55), C(0xFF3B2E22), C(0xFFFBF7EF)),
        ["Sakura"] = new(C(0xFFFBF1F3), C(0xFFFFFAFB), C(0xFFF3E1E6), C(0xFFECD5DC), C(0xFFE0C3CC),
                         C(0x1A5A2A3A), C(0xFF2D1A20), C(0xFF86626D), C(0xFF5A2A3A), C(0xFFFFFFFF)),
    };

    // "White" = the theme's neutral accent (white on dark themes, near-black on Light).
    private static readonly Dictionary<string, Color> Accents = new()
    {
        ["Blue"] = C(0xFF4C8DFF),
        ["Violet"] = C(0xFF9B7BFF),
        ["Green"] = C(0xFF3DD68C),
        ["Pink"] = C(0xFFFF6FAE),
        ["Orange"] = C(0xFFFF9F43),
    };

    /// <summary>Swatch color for the accent picker (on the current theme).</summary>
    public static Color AccentSwatch(string accent, string theme) =>
        TryAccentColor(accent, out var color) ? color : PaletteFor(theme).NeutralAccent;

    /// <summary>A named colored accent, or a custom "#RRGGBB" one from the color wheel.</summary>
    public static bool TryAccentColor(string accent, out Color color)
    {
        if (Accents.TryGetValue(accent, out color))
            return true;
        if (accent.Length == 7 && accent[0] == '#' && uint.TryParse(accent.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var rgb))
        {
            color = C(0xFF000000 | rgb);
            return true;
        }
        return false;
    }

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    // Dark content on light accents, white on dark ones (custom colors can be anything).
    private static Color OnColor(Color c) =>
        0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B > 150 ? C(0xFF141517) : C(0xFFFFFFFF);

    private static Palette PaletteFor(string theme) => Palettes.TryGetValue(theme, out var p) ? p : Palettes["Dark"];

    // Brush -> role, captured once from the untouched (Dark) resources.
    private static List<(SolidColorBrush Brush, Role Role)>? s_brushes;

    /// <summary>The accent color in use (after <see cref="Apply"/>), e.g. for the visualizer.</summary>
    public static Color CurrentAccent { get; private set; } = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

    public static void Apply(string theme, string accent)
    {
        s_brushes ??= CaptureBrushes();

        var palette = PaletteFor(theme);
        var colored = TryAccentColor(accent, out var accentColor);
        if (!colored)
            accentColor = palette.NeutralAccent;
        CurrentAccent = accentColor;
        // The preset colors are all bright, so dark content on them reads best.
        var onAccent = !colored ? palette.OnNeutralAccent
            : Accents.ContainsKey(accent) ? C(0xFF141517) : OnColor(accentColor);

        foreach (var (brush, role) in s_brushes)
        {
            brush.Color = role switch
            {
                Role.Background => palette.Background,
                Role.Panel => palette.Panel,
                Role.Raised => palette.Raised,
                Role.Hover => palette.Hover,
                Role.Border => palette.Border,
                Role.Accent => accentColor,
                Role.AccentDim => palette.AccentDim,
                Role.OnAccent => onAccent,
                Role.TextPrimary => palette.TextPrimary,
                _ => palette.TextSecondary,
            };
        }
    }

    private static List<(SolidColorBrush, Role)> CaptureBrushes()
    {
        var dark = Palettes["Dark"];
        var byColor = new Dictionary<Color, Role>
        {
            [dark.Background] = Role.Background,
            [dark.Panel] = Role.Panel,
            [dark.Raised] = Role.Raised,
            [dark.Hover] = Role.Hover,
            [dark.Border] = Role.Border,
            [dark.NeutralAccent] = Role.Accent,
            [dark.AccentDim] = Role.AccentDim,
            [dark.TextPrimary] = Role.TextPrimary,
            [dark.TextSecondary] = Role.TextSecondary,
        };

        // Only App.xaml's own dictionary (all LunaDrome brushes live there; the merged framework
        // theme dictionaries are huge and aren't ours to recolor). Keys are copied first:
        // reading a value makes Uno materialize it, which breaks a running enumeration.
        var resources = Application.Current.Resources;
        var result = new List<(SolidColorBrush, Role)>();
        foreach (var key in resources.Keys.ToList())
        {
            if (resources[key] is not SolidColorBrush brush)
                continue;
            // OnAccent shares the panel color in Dark, so pick it out by name: content drawn
            // on top of accent fills (play button icon, checkbox tick).
            var name = key as string ?? "";
            if (name.Contains("OnAccent") || name.Contains("CheckGlyph"))
                result.Add((brush, Role.OnAccent));
            else if (byColor.TryGetValue(brush.Color, out var role))
                result.Add((brush, role));
        }
        return result;
    }
}
