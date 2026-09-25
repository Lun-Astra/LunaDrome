using LunaDrome.Core.Services;

namespace LunaDrome.Services;

/// <summary>
/// Downloads the next queue entry to a temp file while the current one plays, so skipping or
/// auto-advancing to it starts instantly from disk instead of waiting on the server (and on
/// Navidrome's transcoder). Only the current + next tracks are kept on disk.
/// </summary>
internal sealed class TrackPrefetcher : IDisposable
{
    private static readonly string CacheDir = Path.Combine(Path.GetTempPath(), "LunaDrome", "prefetch");

    private readonly NavidromeClient _client;
    private CancellationTokenSource? _cts;

    public TrackPrefetcher(NavidromeClient client)
    {
        _client = client;
        // Leftovers from a previous run (the app never gets to clean up on a crash).
        try
        {
            if (Directory.Exists(CacheDir))
                Directory.Delete(CacheDir, recursive: true);
        }
        catch { /* a file still open elsewhere - harmless */ }
    }

    /// <summary>Song ID currently downloading or downloaded.</summary>
    public string? TargetSongId { get; private set; }

    private static string PathFor(string songId) =>
        Path.Combine(CacheDir, string.Concat(songId.Where(char.IsLetterOrDigit)) + ".audio");

    /// <summary>The fully downloaded file for this song, or null if it isn't ready.</summary>
    public string? TryGetFile(string songId)
    {
        var path = PathFor(songId);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Starts downloading <paramref name="songId"/> (dropping any other prefetch), keeping
    /// <paramref name="keepSongId"/>'s file - the one that's playing - on disk.</summary>
    public void Prefetch(string songId, string? keepSongId)
    {
        if (TargetSongId == songId)
            return;

        _cts?.Cancel();
        TargetSongId = songId;
        DeleteAllExcept(songId, keepSongId);

        if (TryGetFile(songId) != null)
            return;

        var cts = _cts = new CancellationTokenSource();
        _ = DownloadAsync(songId, cts.Token);
    }

    private async Task DownloadAsync(string songId, CancellationToken token)
    {
        var path = PathFor(songId);
        var partial = path + ".part";
        try
        {
            // Let the track that just started get the bandwidth first.
            await Task.Delay(TimeSpan.FromSeconds(3), token);
            Directory.CreateDirectory(CacheDir);
            await using (var file = File.Create(partial))
                await _client.DownloadStreamAsync(songId, file, token);
            File.Move(partial, path, overwrite: true);
            Log($"prefetched {songId}");
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
            {
                Log($"prefetch of {songId} failed: {ex.Message}");
                // Retry on the next Prefetch call for this song rather than never.
                if (TargetSongId == songId)
                    TargetSongId = null;
            }
            TryDelete(partial);
        }
    }

    private static void DeleteAllExcept(params string?[] songIds)
    {
        if (!Directory.Exists(CacheDir))
            return;

        var keep = songIds.Where(id => id != null).Select(id => PathFor(id!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(CacheDir))
        {
            if (!keep.Contains(file) && !file.EndsWith(".part", StringComparison.Ordinal))
                TryDelete(file);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { /* still open by the player - removed on a later pass or next launch */ }
    }

    private static void Log(string message)
    {
        if (Environment.GetEnvironmentVariable("LUNADROME_LOG") == "1")
            Console.Error.WriteLine($"[LunaDrome] {DateTime.Now:HH:mm:ss.fff} {message}");
    }

    public void Dispose()
    {
        _cts?.Cancel();
        TargetSongId = null;
    }
}
