using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Musicplayer.App.Services;

public class AppSettings
{
    public string ServerUrl { get; set; } = "";
    public string Username { get; set; } = "";

    // DPAPI-encrypted (CurrentUser scope), base64-encoded. Only decryptable by this
    // Windows account on this machine — never sent anywhere, never plain text on disk.
    public string? ProtectedPassword { get; set; }

    public double Volume { get; set; } = 0.8;
}

/// <summary>
/// Persists login convenience fields (server URL, username, and optionally an
/// encrypted password) so the login screen can pre-fill or auto sign in.
/// </summary>
public static class SettingsService
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LunaDrome");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LunaDrome.NavidromeCredentials.v1");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(SettingsDir);
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }

    public static string ProtectPassword(string password)
    {
        var bytes = Encoding.UTF8.GetBytes(password);
        var encrypted = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    public static string? UnprotectPassword(string? protectedPassword)
    {
        if (string.IsNullOrEmpty(protectedPassword))
            return null;

        try
        {
            var encrypted = Convert.FromBase64String(protectedPassword);
            var bytes = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            // Can happen if the settings file was copied to another machine/user account.
            return null;
        }
    }
}
