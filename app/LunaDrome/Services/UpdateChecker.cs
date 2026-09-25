using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace LunaDrome.Services;

public sealed record UpdateInfo(Version Version, string PageUrl, string? InstallerUrl);

/// <summary>
/// Built-in updates (#5): compares this build's version with the latest GitHub release. On a
/// Windows install made by the setup program, the new setup is downloaded and run silently
/// (it closes LunaDrome, upgrades in place and starts it again); anywhere else (Linux packages,
/// a copied folder) the release page is opened instead.
/// </summary>
public static class UpdateChecker
{
    private const string LatestReleaseApi = "https://api.github.com/repos/Lun-Astra/LunaDrome/releases/latest";

    // ApplicationDisplayVersion from the csproj, embedded as AssemblyMetadata("AppVersion").
    public static Version CurrentVersion { get; } = ReadCurrentVersion();

    private static Version ReadCurrentVersion()
    {
        var text = typeof(UpdateChecker).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "AppVersion")?.Value;
        return Version.TryParse(text, out var v) ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : new Version(0, 0, 0);
    }

    /// <summary>Installed by LunaDrome-Setup (Inno Setup leaves its uninstaller next to the exe).</summary>
    public static bool CanInstallInPlace =>
        OperatingSystem.IsWindows() && File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        // GitHub's API rejects requests without a User-Agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"LunaDrome/{CurrentVersion}");
        return http;
    }

    /// <summary>The latest release if it's newer than this build, else null (also on any error).</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            // A build without a version stamped in (0.0.0) can't be compared meaningfully.
            if (CurrentVersion.Major == 0)
                return null;

            using var http = CreateClient(TimeSpan.FromSeconds(15));
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var doc = JsonDocument.Parse(await http.GetStringAsync(LatestReleaseApi));
            var root = doc.RootElement;

            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
                return null;
            latest = new Version(latest.Major, latest.Minor, Math.Max(latest.Build, 0));
            if (latest <= CurrentVersion)
                return null;

            string? installerUrl = null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.StartsWith("LunaDrome-Setup-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    installerUrl = asset.GetProperty("browser_download_url").GetString();
            }

            var page = root.GetProperty("html_url").GetString() ?? "https://github.com/Lun-Astra/LunaDrome/releases/latest";
            return new UpdateInfo(latest, page, installerUrl);
        }
        catch (Exception ex)
        {
            if (Environment.GetEnvironmentVariable("LUNADROME_LOG") == "1")
                Console.Error.WriteLine($"[LunaDrome] update check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Downloads the setup program to temp; progress is 0..1.</summary>
    public static async Task<string> DownloadInstallerAsync(UpdateInfo update, IProgress<double> progress)
    {
        var url = update.InstallerUrl ?? throw new InvalidOperationException("This release has no Windows installer.");
        var dir = Path.Combine(Path.GetTempPath(), "LunaDrome", "update");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Path.GetFileName(new Uri(url).LocalPath));

        using var http = CreateClient(TimeSpan.FromMinutes(10));
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;

        await using (var source = await response.Content.ReadAsStreamAsync())
        await using (var file = File.Create(path + ".part"))
        {
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                if (total > 0)
                    progress.Report((double)done / total);
            }
        }

        File.Move(path + ".part", path, overwrite: true);
        return path;
    }

    /// <summary>Starts the setup silently; the caller should exit right after so it can replace the files.</summary>
    public static void RunInstaller(string installerPath) =>
        Process.Start(new ProcessStartInfo(installerPath, "/SILENT /SP- /NOCANCEL /CLOSEAPPLICATIONS") { UseShellExecute = true });

    public static void OpenReleasePage(UpdateInfo update) =>
        Process.Start(new ProcessStartInfo(update.PageUrl) { UseShellExecute = true });
}
