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
    public static readonly string[] ThemeNames = { "Dark", "Midnight", "Black", "Light" };
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
        ["Light"] = new(C(0xFFF3F4F6), C(0xFFFFFFFF), C(0xFFE6E8EC), C(0xFFDCDFE4), C(0xFFD0D4DA),
                        C(0x1A000000), C(0xFF1A1B1E), C(0xFF5F636B), C(0xFF1A1B1E), C(0xFFFFFFFF)),
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
        Accents.TryGetValue(accent, out var color) ? color : PaletteFor(theme).NeutralAccent;

    private static Palette PaletteFor(string theme) => Palettes.TryGetValue(theme, out var p) ? p : Palettes["Dark"];

    // Brush -> role, captured once from the untouched (Dark) resources.
    private static List<(SolidColorBrush Brush, Role Role)>? s_brushes;

    public static void Apply(string theme, string accent)
    {
        s_brushes ??= CaptureBrushes();

        var palette = PaletteFor(theme);
        var accentColor = Accents.TryGetValue(accent, out var a) ? a : palette.NeutralAccent;
        // Colored accents are all bright, so dark content on them reads best.
        var onAccent = Accents.ContainsKey(accent) ? C(0xFF141517) : palette.OnNeutralAccent;

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
