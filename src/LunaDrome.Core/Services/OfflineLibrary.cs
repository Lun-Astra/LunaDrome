using System.Text.Json;
using LunaDrome.Core.Models;

namespace LunaDrome.Core.Services;

/// <summary>A song kept on this device for offline listening.</summary>
public sealed class OfflineSong
{
    public Song Song { get; set; } = new();
    /// <summary>File name in the offline folder.</summary>
    public string File { get; set; } = "";
    public StreamQuality Quality { get; set; }
    public long Size { get; set; }
    public DateTime DownloadedAt { get; set; }
    /// <summary>Downloaded by itself (not only as part of an album or playlist): stays when those go.</summary>
    public bool Single { get; set; }
}

/// <summary>An album or playlist downloaded as a whole. A playlist follows the server: songs
/// added there are downloaded, songs taken out are removed (<see cref="OfflineLibrary.SyncAsync"/>).</summary>
public sealed class OfflineCollection
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "playlist";   // "playlist" | "album"
    public string Name { get; set; } = "";
    public string Subtitle { get; set; } = "";
    /// <summary>Album id of the cover shown for it (its first song's album).</summary>
    public string CoverAlbumId { get; set; } = "";
    public List<string> SongIds { get; set; } = new();
}

/// <summary>
/// Offline downloads (#28): songs, albums and playlists stored in the app's own folder, in the
/// download quality picked in Settings (separate from streaming quality, so a phone can stream
/// in Original at home but keep small Opus files). One song downloads at a time, in the
/// background; index.json lists what's there, with each song's details, so the Downloaded page
/// and playback work without the server. Covers are kept per album.
/// </summary>
public sealed class OfflineLibrary
{
    private sealed class Index
    {
        public Dictionary<string, OfflineSong> Songs { get; set; } = new();
        public List<OfflineCollection> Collections { get; set; } = new();
        /// <summary>Asked for but not downloaded yet, in order - kept so closing the app
        /// halfway an album doesn't forget the rest.</summary>
        public List<WaitingSong> Waiting { get; set; } = new();
    }

    private sealed class WaitingSong
    {
        public Song Song { get; set; } = new();
        public bool Single { get; set; }
    }

    private readonly string _root;
    private readonly string _indexPath;
    private readonly NavidromeClient _client;
    private readonly object _lock = new();
    private readonly Index _index;
    private CancellationTokenSource? _workerCts;
    private Task? _worker;

    public OfflineLibrary(string root, NavidromeClient client)
    {
        _root = root;
        _indexPath = Path.Combine(root, "index.json");
        _client = client;
        _index = LoadIndex();
    }

    /// <summary>Starts (or continues) downloading what's waiting - after signing in.</summary>
    public void Resume() => Start();

    /// <summary>Quality for new downloads (Settings > Downloads).</summary>
    public StreamQuality Quality { get; set; } = StreamQuality.High;

    /// <summary>Something changed: a song finished, the queue moved, progress. Raised on a
    /// background thread - marshal to the UI thread.</summary>
    public event Action? Changed;

    /// <summary>The song downloading now, and how many bytes of it have arrived.</summary>
    public Song? Downloading { get; private set; }
    public long DownloadingBytes { get; private set; }
    /// <summary>The last download that failed, and why (cleared once one succeeds).</summary>
    public string? LastError { get; private set; }

    /// <summary>Songs still to download, the one downloading now included.</summary>
    public int PendingCount
    {
        get { lock (_lock) return _index.Waiting.Count; }
    }

    public string CoversFolder => Path.Combine(_root, "covers");

    // ----- What's there -----

    public bool IsDownloaded(string songId)
    {
        lock (_lock) return _index.Songs.ContainsKey(songId);
    }

    public bool IsQueued(string songId)
    {
        lock (_lock) return _index.Waiting.Any(w => w.Song.Id == songId);
    }

    /// <summary>The song's file on this device, or null.</summary>
    public string? TryGetFile(string songId)
    {
        OfflineSong? entry;
        lock (_lock)
            _index.Songs.TryGetValue(songId, out entry);
        if (entry == null)
            return null;
        var path = Path.Combine(_root, entry.File);
        return System.IO.File.Exists(path) ? path : null;
    }

    /// <summary>The kept cover of an album, or null.</summary>
    public string? CoverFile(string albumId)
    {
        if (string.IsNullOrEmpty(albumId))
            return null;
        var path = Path.Combine(CoversFolder, SafeName(albumId) + ".jpg");
        return System.IO.File.Exists(path) ? path : null;
    }

    /// <summary>Every downloaded song, newest first.</summary>
    public List<OfflineSong> Songs()
    {
        lock (_lock) return _index.Songs.Values.OrderByDescending(s => s.DownloadedAt).ToList();
    }

    public List<OfflineCollection> Collections()
    {
        lock (_lock) return _index.Collections.ToList();
    }

    public bool HasCollection(string id)
    {
        lock (_lock) return _index.Collections.Any(c => c.Id == id);
    }

    public long SizeBytes()
    {
        lock (_lock) return _index.Songs.Values.Sum(s => s.Size);
    }

    public bool IsEmpty
    {
        get { lock (_lock) return _index.Songs.Count == 0 && _index.Collections.Count == 0; }
    }

    // ----- Adding -----

    /// <summary>Downloads these songs by themselves.</summary>
    public void DownloadSongs(IEnumerable<Song> songs)
    {
        lock (_lock)
        {
            foreach (var song in songs)
            {
                if (_index.Songs.TryGetValue(song.Id, out var have))
                    have.Single = true;
                else if (_index.Waiting.FirstOrDefault(w => w.Song.Id == song.Id) is { } waiting)
                    waiting.Single = true;
                else
                    _index.Waiting.Add(new WaitingSong { Song = Stored(song), Single = true });
            }
            SaveLocked();
        }
        Start();
    }

    /// <summary>Downloads an album or playlist (or updates the one that's there).</summary>
    public void DownloadCollection(OfflineCollection collection, IReadOnlyList<Song> songs)
    {
        lock (_lock)
        {
            collection.SongIds = songs.Select(s => s.Id).Distinct().ToList();
            if (string.IsNullOrEmpty(collection.CoverAlbumId))
                collection.CoverAlbumId = songs.FirstOrDefault()?.AlbumId ?? "";
            _index.Collections.RemoveAll(c => c.Id == collection.Id);
            _index.Collections.Add(collection);
            foreach (var song in songs)
            {
                if (!_index.Songs.ContainsKey(song.Id) && !_index.Waiting.Any(w => w.Song.Id == song.Id))
                    _index.Waiting.Add(new WaitingSong { Song = Stored(song) });
            }
            DropUnusedLocked();
            SaveLocked();
        }
        Start();
    }

    /// <summary>Brings downloaded playlists up to date with the server (new songs downloaded,
    /// removed ones deleted) and finishes anything an earlier run didn't. Online only.</summary>
    public async Task SyncAsync(Func<string, Task<List<Song>>> getPlaylistSongs)
    {
        foreach (var collection in Collections())
        {
            if (collection.Kind != "playlist")
                continue;
            try
            {
                var songs = await getPlaylistSongs(collection.Id);
                DownloadCollection(collection, songs);
            }
            catch
            {
                // Deleted on the server, or no connection: keep what's there.
            }
        }
        Start();
    }

    // What's kept of a song: no server cover URL (the page uses the kept cover file).
    private static Song Stored(Song song)
    {
        var copy = song.Clone();
        copy.CoverArtUrl = null;
        return copy;
    }

    // ----- Removing -----

    public void RemoveSongs(IEnumerable<string> songIds)
    {
        lock (_lock)
        {
            foreach (var id in songIds)
            {
                CancelPendingLocked(id);
                foreach (var collection in _index.Collections)
                    collection.SongIds.Remove(id);
                DeleteSongLocked(id);
            }
            _index.Collections.RemoveAll(c => c.SongIds.Count == 0);
            SaveLocked();
        }
        Changed?.Invoke();
    }

    public void RemoveCollection(string id)
    {
        lock (_lock)
        {
            _index.Collections.RemoveAll(c => c.Id == id);
            DropUnusedLocked();
            SaveLocked();
        }
        Changed?.Invoke();
    }

    public void RemoveAll()
    {
        _workerCts?.Cancel();
        lock (_lock)
        {
            _index.Waiting.Clear();
            _index.Songs.Clear();
            _index.Collections.Clear();
            SaveLocked();
        }
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*.audio"))
                TryDelete(file);
            if (Directory.Exists(CoversFolder))
                Directory.Delete(CoversFolder, recursive: true);
        }
        catch { /* a file the player has open: it goes next time */ }
        Changed?.Invoke();
    }

    // A song stays while it's a single or in a downloaded collection.
    private void DropUnusedLocked()
    {
        var wanted = _index.Collections.SelectMany(c => c.SongIds).ToHashSet();
        foreach (var id in _index.Songs.Where(kv => !kv.Value.Single && !wanted.Contains(kv.Key)).Select(kv => kv.Key).ToList())
            DeleteSongLocked(id);
        foreach (var waiting in _index.Waiting.Where(w => !w.Single && !wanted.Contains(w.Song.Id)).ToList())
            CancelPendingLocked(waiting.Song.Id);
    }

    private void CancelPendingLocked(string id)
    {
        _index.Waiting.RemoveAll(w => w.Song.Id == id);
        if (Downloading?.Id == id)
            _currentCts?.Cancel();
    }

    private void DeleteSongLocked(string id)
    {
        if (!_index.Songs.Remove(id, out var entry))
            return;
        TryDelete(Path.Combine(_root, entry.File));
        // The album's cover goes with its last song.
        if (!_index.Songs.Values.Any(s => s.Song.AlbumId == entry.Song.AlbumId))
            TryDelete(Path.Combine(CoversFolder, SafeName(entry.Song.AlbumId) + ".jpg"));
    }

    // ----- The download worker -----

    private CancellationTokenSource? _currentCts;

    private void Start()
    {
        Changed?.Invoke();
        lock (_lock)
        {
            if (_worker is { IsCompleted: false } || _index.Waiting.Count == 0)
                return;
            _workerCts = new CancellationTokenSource();
            var token = _workerCts.Token;
            _worker = Task.Run(() => RunAsync(token));
        }
    }

    // Waiting songs in order; one that fails moves to the back and is tried again later, but
    // each only once per run, so a dead connection doesn't spin.
    private async Task RunAsync(CancellationToken token)
    {
        var tried = new HashSet<string>();
        while (!token.IsCancellationRequested)
        {
            WaitingSong next;
            StreamQuality quality;
            CancellationTokenSource current;
            lock (_lock)
            {
                var candidate = _index.Waiting.FirstOrDefault(w => !tried.Contains(w.Song.Id));
                if (candidate == null)
                    break;
                next = candidate;
                tried.Add(next.Song.Id);
                Downloading = next.Song;
                DownloadingBytes = 0;
                quality = Quality;
                current = _currentCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            }
            Changed?.Invoke();
            var ok = await DownloadOneAsync(next, quality, current.Token);
            lock (_lock)
            {
                Downloading = null;
                if (!ok && _index.Waiting.Remove(next))
                    _index.Waiting.Add(next);   // to the back
                SaveLocked();
            }
            Changed?.Invoke();
        }
        lock (_lock)
            Downloading = null;
        Changed?.Invoke();
    }

    private async Task<bool> DownloadOneAsync(WaitingSong waiting, StreamQuality quality, CancellationToken token)
    {
        var song = waiting.Song;
        var name = SafeName(song.Id) + ".audio";
        var path = Path.Combine(_root, name);
        var partial = path + ".part";
        try
        {
            Directory.CreateDirectory(_root);
            var progress = new Progress<long>(bytes => { DownloadingBytes = bytes; Changed?.Invoke(); });
            await using (var file = System.IO.File.Create(partial))
                await _client.DownloadStreamAsync(song.Id, file, token, quality, new ThrottledProgress(progress));
            System.IO.File.Move(partial, path, overwrite: true);
            await SaveCoverAsync(song.AlbumId, token);

            lock (_lock)
            {
                if (!_index.Waiting.Remove(waiting))
                {
                    TryDelete(path);   // removed while it was downloading
                    return true;
                }
                _index.Songs[song.Id] = new OfflineSong
                {
                    Song = song, File = name, Quality = quality, Size = new FileInfo(path).Length,
                    DownloadedAt = DateTime.UtcNow, Single = waiting.Single,
                };
                LastError = null;
                SaveLocked();
            }
            return true;
        }
        catch (Exception ex)
        {
            TryDelete(partial);
            if (ex is not OperationCanceledException)
                LastError = $"{song.Title}: {ex.Message}";
            return false;
        }
    }

    private async Task SaveCoverAsync(string albumId, CancellationToken token)
    {
        if (string.IsNullOrEmpty(albumId) || CoverFile(albumId) != null)
            return;
        try
        {
            var bytes = await _client.GetCoverArtBytesAsync(albumId, 480, token);
            Directory.CreateDirectory(CoversFolder);
            await System.IO.File.WriteAllBytesAsync(Path.Combine(CoversFolder, SafeName(albumId) + ".jpg"), bytes, token);
        }
        catch
        {
            // No cover: the page shows a blank square.
        }
    }

    // At most ~5 progress reports a second: each one redraws the Downloaded page's status.
    private sealed class ThrottledProgress(IProgress<long> inner) : IProgress<long>
    {
        private long _last;
        public void Report(long value)
        {
            var now = Environment.TickCount64;
            if (now - _last < 200)
                return;
            _last = now;
            inner.Report(value);
        }
    }

    // ----- Storage -----

    private Index LoadIndex()
    {
        try
        {
            if (System.IO.File.Exists(_indexPath))
            {
                return JsonSerializer.Deserialize<Index>(System.IO.File.ReadAllText(_indexPath)) ?? new Index();
            }
        }
        catch
        {
            // A broken index: start over (the files get re-downloaded when asked for).
        }
        return new Index();
    }

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(_root);
            var json = JsonSerializer.Serialize(_index);
            var temp = _indexPath + ".tmp";
            System.IO.File.WriteAllText(temp, json);
            System.IO.File.Move(temp, _indexPath, overwrite: true);
        }
        catch
        {
            // Disk full or similar: the files stay, the index is written at the next change.
        }
    }

    private static string SafeName(string id) => string.Concat(id.Where(char.IsLetterOrDigit));

    private static void TryDelete(string path)
    {
        try { System.IO.File.Delete(path); } catch { /* still open: harmless */ }
    }
}
