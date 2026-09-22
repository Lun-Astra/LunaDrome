using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LunaDrome.Core.Services;

public class AppSettings
{
    public string ServerUrl { get; set; } = "";
    public string Username { get; set; } = "";

    // AES-256-GCM encrypted (key in a local, per-user key file - see CredentialProtector),
    // base64-encoded. Not tied to a specific OS API (unlike Windows DPAPI), so this works
    // identically on Windows and Linux.
    public string? ProtectedPassword { get; set; }
}

/// <summary>
/// Persists login convenience fields (server URL, username, and optionally an encrypted
/// password) so the login screen can pre-fill or auto sign in. Cross-platform: uses plain
/// BCL APIs only (Environment.GetFolderPath's ApplicationData maps to %AppData% on Windows
/// and ~/.config on Linux/macOS via .NET's own runtime, no OS-specific code needed here).
/// </summary>
public static class SettingsService
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LunaDrome-Uno");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

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
}

/// <summary>
/// AES-256-GCM encryption for the saved password, keyed by a randomly-generated key file
/// created on first use in the same settings folder. Not tied to a Windows-only API like
/// DPAPI, so the exact same code path works on Linux. The key file is the sole secret - any
/// process running as this OS user could read it, the same threat model DPAPI's CurrentUser
/// scope already has (any process running as that user can call Unprotect too).
/// </summary>
public static class CredentialProtector
{
    private static readonly string KeyDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LunaDrome-Uno");

    private static readonly string KeyPath = Path.Combine(KeyDir, "credkey");

    private const int KeySizeBytes = 32;
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;

    private static byte[] GetOrCreateKey()
    {
        Directory.CreateDirectory(KeyDir);

        if (File.Exists(KeyPath))
            return Convert.FromBase64String(File.ReadAllText(KeyPath));

        var key = RandomNumberGenerator.GetBytes(KeySizeBytes);
        File.WriteAllText(KeyPath, Convert.ToBase64String(key));

        if (!OperatingSystem.IsWindows())
        {
            // Best-effort: restrict the key file to the owning user on Linux/macOS.
            // No-ops harmlessly if unsupported; NTFS ACLs already cover Windows by default.
            try { File.SetUnixFileMode(KeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch { /* not fatal - the file still exists, just with default permissions */ }
        }

        return key;
    }

    public static string Protect(string plaintext)
    {
        var key = GetOrCreateKey();
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagSizeBytes];

        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        var result = new byte[NonceSizeBytes + TagSizeBytes + cipherBytes.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceSizeBytes);
        Buffer.BlockCopy(tag, 0, result, NonceSizeBytes, TagSizeBytes);
        Buffer.BlockCopy(cipherBytes, 0, result, NonceSizeBytes + TagSizeBytes, cipherBytes.Length);

        return Convert.ToBase64String(result);
    }

    public static string? Unprotect(string? protectedText)
    {
        if (string.IsNullOrEmpty(protectedText))
            return null;

        try
        {
            if (!File.Exists(KeyPath))
                return null;

            var key = Convert.FromBase64String(File.ReadAllText(KeyPath));
            var all = Convert.FromBase64String(protectedText);

            var nonce = all.AsSpan(0, NonceSizeBytes);
            var tag = all.AsSpan(NonceSizeBytes, TagSizeBytes);
            var cipherBytes = all.AsSpan(NonceSizeBytes + TagSizeBytes);
            var plainBytes = new byte[cipherBytes.Length];

            using var aes = new AesGcm(key, TagSizeBytes);
            aes.Decrypt(nonce, cipherBytes, tag, plainBytes);

            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            // Can happen if the key file or settings were copied to another machine.
            return null;
        }
    }
}
