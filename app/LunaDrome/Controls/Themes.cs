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
    public static readonly string[] ThemeNames =
    {
        "Dark", "Midnight", "Black", "Nord", "Mocha", "Forest", "Wine", "Ocean", "Dracula", "Slate", "Ember",
        "Light", "Creme", "Sakura", "Mint", "Sky", "Solarized",
    };

    /// <summary>Light background (the Settings page lists these on their own row).</summary>
    public static bool IsLight(string theme) => Luma(PaletteFor(theme).Background) > 128;
    public static readonly string[] AccentNames =
        { "White", "Blue", "Sky", "Teal", "Green", "Lime", "Yellow", "Orange", "Red", "Pink", "Violet", "Lavender" };

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
        ["Ocean"] = new(C(0xFF0F1E26), C(0xFF0A161C), C(0xFF172A34), C(0xFF1E3440), C(0xFF294452),
                        C(0x26FFFFFF), C(0xFFE3F1F6), C(0xFF86A3B0), C(0xFFE3F1F6), C(0xFF0A161C)),
        ["Dracula"] = new(C(0xFF282A36), C(0xFF21222C), C(0xFF343746), C(0xFF3C3F51), C(0xFF4A4D63),
                          C(0x26FFFFFF), C(0xFFF8F8F2), C(0xFF9EA1BA), C(0xFFF8F8F2), C(0xFF21222C)),
        ["Slate"] = new(C(0xFF1E2329), C(0xFF181C21), C(0xFF283039), C(0xFF2F3843), C(0xFF3B4552),
                        C(0x26FFFFFF), C(0xFFE8ECF0), C(0xFF8F9AA6), C(0xFFE8ECF0), C(0xFF181C21)),
        ["Ember"] = new(C(0xFF221813), C(0xFF1B120E), C(0xFF30221A), C(0xFF3A2920), C(0xFF4A3529),
                        C(0x26FFFFFF), C(0xFFF6E8DE), C(0xFFAB9283), C(0xFFF6E8DE), C(0xFF1B120E)),
        ["Mint"] = new(C(0xFFEFF7F2), C(0xFFFAFDFB), C(0xFFDDEDE3), C(0xFFD1E6D9), C(0xFFC0DACA),
                       C(0x1A1F4A33), C(0xFF16261D), C(0xFF5D7667), C(0xFF1F4A33), C(0xFFFFFFFF)),
        ["Sky"] = new(C(0xFFEEF4FA), C(0xFFFAFCFE), C(0xFFDCE7F3), C(0xFFD0DFEE), C(0xFFBFD2E6),
                      C(0x1A1E3A5A), C(0xFF152233), C(0xFF5B6E84), C(0xFF1E3A5A), C(0xFFFFFFFF)),
        ["Solarized"] = new(C(0xFFFDF6E3), C(0xFFFFFBF0), C(0xFFEEE8D5), C(0xFFE6DFC8), C(0xFFD9D1B8),
                            C(0x1A073642), C(0xFF073642), C(0xFF657B83), C(0xFF073642), C(0xFFFFFBF0)),
    };

    // "White" = the theme's neutral accent (white on dark themes, near-black on Light).
    private static readonly Dictionary<string, Color> Accents = new()
    {
        ["Blue"] = C(0xFF4C8DFF),
        ["Sky"] = C(0xFF4FC3F7),
        ["Teal"] = C(0xFF2EC4B6),
        ["Green"] = C(0xFF3DD68C),
        ["Lime"] = C(0xFFA3E635),
        ["Yellow"] = C(0xFFFFD43B),
        ["Orange"] = C(0xFFFF9F43),
        ["Red"] = C(0xFFFF5C5C),
        ["Pink"] = C(0xFFFF6FAE),
        ["Violet"] = C(0xFF9B7BFF),
        ["Lavender"] = C(0xFFC4B5FD),
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

    private static double Luma(Color c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;

    // Dark content on light accents, white on dark ones (custom colors can be anything).
    private static Color OnColor(Color c) => Luma(c) > 150 ? C(0xFF141517) : C(0xFFFFFFFF);

    /// <summary>A named theme, or a custom "#RRGGBB" one: the whole palette worked out from
    /// that one background color.</summary>
    private static Palette PaletteFor(string theme) =>
        Palettes.TryGetValue(theme, out var p) ? p
        : TryAccentColor(theme, out var custom) && !Accents.ContainsKey(theme) ? Derive(custom)
        : Palettes["Dark"];

    /// <summary>The background color a theme starts from (the custom-theme swatch).</summary>
    public static Color ThemeBackground(string theme) => PaletteFor(theme).Background;

    private static Color Mix(Color from, Color to, double amount) => Color.FromArgb(0xFF,
        (byte)Math.Round(from.R + (to.R - from.R) * amount),
        (byte)Math.Round(from.G + (to.G - from.G) * amount),
        (byte)Math.Round(from.B + (to.B - from.B) * amount));

    // Same steps the built-in themes take from their background: on a dark one the panels are a
    // bit darker and raised surfaces lighter; on a light one panels go toward white and raised
    // surfaces toward the text color. Text is the opposite end, tinted with the background.
    private static Palette Derive(Color bg)
    {
        var black = C(0xFF000000);
        var white = C(0xFFFFFFFF);
        if (Luma(bg) <= 128)
        {
            var text = Mix(white, bg, 0.08);
            var panel = Mix(bg, black, 0.22);
            return new(bg, panel, Mix(bg, white, 0.06), Mix(bg, white, 0.10), Mix(bg, white, 0.17),
                       C(0x26FFFFFF), text, Mix(white, bg, 0.45), text, panel);
        }
        var ink = Mix(black, bg, 0.12);
        var light = Mix(bg, white, 0.6);
        return new(bg, light, Mix(bg, ink, 0.06), Mix(bg, ink, 0.10), Mix(bg, ink, 0.17),
                   C(0x1A000000), ink, Mix(ink, bg, 0.5), ink, light);
    }

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
