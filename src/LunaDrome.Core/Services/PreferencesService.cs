using System.Text.Json;

namespace LunaDrome.Core.Services;

/// <summary>User preferences from the Settings page. Kept apart from settings.json, which the
/// login screen rewrites on every sign-in.</summary>
public class Preferences
{
    public string Theme { get; set; } = "Dark";
    public string Accent { get; set; } = "White";

    public bool PrefetchNextTrack { get; set; } = true;
    public bool ResumeQueueOnStartup { get; set; } = true;
    public bool OnlineLyrics { get; set; } = true;
    public bool CheckForUpdates { get; set; } = true;

    public double Volume { get; set; } = 0.8;
}

/// <summary>Reads/writes preferences.json in the LunaDrome-Uno settings folder.</summary>
public static class PreferencesService
{
    private static readonly string PreferencesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LunaDrome-Uno", "preferences.json");

    public static Preferences Load()
    {
        try
        {
            return File.Exists(PreferencesPath)
                ? JsonSerializer.Deserialize<Preferences>(File.ReadAllText(PreferencesPath)) ?? new Preferences()
                : new Preferences();
        }
        catch
        {
            return new Preferences();
        }
    }

    public static void Save(Preferences preferences)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencesPath)!);
            File.WriteAllText(PreferencesPath, JsonSerializer.Serialize(preferences, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort: a read-only profile just means preferences don't stick */ }
    }
}
