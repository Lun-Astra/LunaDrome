using System.Text.Json;

namespace LunaDrome.Core.Services;

/// <summary>User preferences from the Settings page. Kept apart from settings.json, which the
/// login screen rewrites on every sign-in.</summary>
public class Preferences
{
    public string Theme { get; set; } = "Dark";
    public string Accent { get; set; } = "White";
    /// <summary>The color-wheel pick ("#RRGGBB"), kept while a preset accent is in use.</summary>
    public string CustomAccent { get; set; } = "";

    public bool PrefetchNextTrack { get; set; } = true;
    public bool ResumeQueueOnStartup { get; set; } = true;
    public bool OnlineLyrics { get; set; } = true;
    public bool CheckForUpdates { get; set; } = true;

    public double Volume { get; set; } = 0.8;

    /// <summary>SpotiDrome web address (http://host:8080); empty = "Download via SpotiDrome" hidden.</summary>
    public string SpotiDromeUrl { get; set; } = "";
    /// <summary>SpotiDrome API key (encrypted with CredentialProtector), created in SpotiDrome > Settings > Access.</summary>
    public string SpotiDromeApiKey { get; set; } = "";

    /// <summary>JamiDrome web address (https://jam.example.com); empty = no Jam page.</summary>
    public string JamiDromeUrl { get; set; } = "";
    /// <summary>JamiDrome host token (encrypted with CredentialProtector), from signing in there.</summary>
    public string JamiDromeHostToken { get; set; } = "";
    /// <summary>This install's id as a jam speaker, so the server can tell devices apart.</summary>
    public string JamDeviceId { get; set; } = "";
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
