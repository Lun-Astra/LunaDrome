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
    /// <summary>Show the playing song on the user's Discord profile (desktop). Off by default.</summary>
    public bool DiscordPresence { get; set; }

    public double Volume { get; set; } = 0.8;

    /// <summary>Streaming quality (#28): converted by Navidrome for anything but Original.</summary>
    public StreamQuality StreamQuality { get; set; } = StreamQuality.Original;

    /// <summary>Volume change per mouse-wheel notch over the volume slider, in percent (#27).</summary>
    public int VolumeScrollStep { get; set; } = 5;

    /// <summary>Crossfade between songs in seconds (desktop); 0 = off.</summary>
    public int CrossfadeSeconds { get; set; }

    /// <summary>Synced-lyrics offset per song id, in seconds (positive = lyrics later), set with
    /// the Earlier / Later buttons in the Lyrics tab.</summary>
    public Dictionary<string, double> LyricsOffsets { get; set; } = new();

    /// <summary>SpotiDrome web address (http://host:8080); empty = "Download via SpotiDrome" hidden.</summary>
    public string SpotiDromeUrl { get; set; } = "";
    /// <summary>SpotiDrome API key (encrypted with CredentialProtector), created in SpotiDrome > Settings > Access.</summary>
    public string SpotiDromeApiKey { get; set; } = "";

    /// <summary>JamiDrome web address (https://jam.example.com); empty = no Jam page.</summary>
    public string JamiDromeUrl { get; set; } = "";
    /// <summary>JamiDrome API key (encrypted with CredentialProtector), made in its Host panel.
    /// (Replaces the old JamiDromeHostToken from the Navidrome sign-in; JamiDrome dropped those.)</summary>
    public string JamiDromeApiKey { get; set; } = "";
    /// <summary>This install's id as a jam speaker, so the server can tell devices apart.</summary>
    public string JamDeviceId { get; set; } = "";

    /// <summary>This install's Sendspin server id: speakers remember the server they last played
    /// from by it, so it stays the same across restarts.</summary>
    public string SendspinServerId { get; set; } = "";
    /// <summary>This install's Sendspin identity for the encrypted protocol: its Curve25519
    /// private key (encrypted with CredentialProtector); the public key is its server_id.</summary>
    public string SendspinServerKey { get; set; } = "";
    /// <summary>Pairings with Sendspin speakers: client_id → long-term PSK (encrypted).</summary>
    public Dictionary<string, string> SendspinPairings { get; set; } = new();
    /// <summary>Sendspin speakers added by address (ws:// URLs), for networks without mDNS.</summary>
    public List<string> SendspinManualSpeakers { get; set; } = new();
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
            var json = Serialize(preferences);
            StateFileWriter.Write(PreferencesPath, () => json);
        }
        catch { /* best-effort: a read-only profile just means preferences don't stick */ }
    }

    /// <summary>Serialized here (the object belongs to the UI thread), written in the background.</summary>
    public static void SaveInBackground(Preferences preferences)
    {
        var json = Serialize(preferences);
        StateFileWriter.WriteInBackground(PreferencesPath, () => json);
    }

    private static string Serialize(Preferences preferences) =>
        JsonSerializer.Serialize(preferences, new JsonSerializerOptions { WriteIndented = true });
}
