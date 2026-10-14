namespace LunaDrome.Services;

/// <summary>
/// playback.log next to preferences.json: playback events, for troubleshooting (Windows has no
/// console to read them from, and on Android it's what "Share playback log" sends). When it passes
/// 1 MB it's moved to playback.old.log. LUNADROME_LOG=1 also prints each line to the terminal.
/// </summary>
public static class PlaybackLog
{
    private static readonly bool ToConsole = Environment.GetEnvironmentVariable("LUNADROME_LOG") == "1";
    private static readonly object Lock = new();

    public static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LunaDrome-Uno", "playback.log");

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        if (ToConsole)
            Console.Error.WriteLine($"[LunaDrome] {line}");
        lock (Lock)
        {
            try
            {
                var file = new FileInfo(Path);
                if (file.Exists && file.Length > 1024 * 1024)
                    File.Move(Path, System.IO.Path.ChangeExtension(Path, ".old.log"), overwrite: true);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(Path, line + Environment.NewLine);
            }
            catch
            {
                // logging must never break playback
            }
        }
    }

    /// <summary>The end of the log (the old file first when the current one is short).</summary>
    public static string ReadRecent(int maxChars = 200_000)
    {
        lock (Lock)
        {
            try
            {
                var old = System.IO.Path.ChangeExtension(Path, ".old.log");
                var text = (File.Exists(old) ? File.ReadAllText(old) : "") + (File.Exists(Path) ? File.ReadAllText(Path) : "");
                return text.Length > maxChars ? text[^maxChars..] : text;
            }
            catch (Exception ex)
            {
                return $"(couldn't read the log: {ex.Message})";
            }
        }
    }
}
