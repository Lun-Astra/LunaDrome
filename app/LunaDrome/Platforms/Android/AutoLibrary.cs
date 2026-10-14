using System.Collections.Concurrent;
using LunaDrome.Core.Models;
using LunaDrome.Core.Services;

namespace LunaDrome.Services;

/// <summary>
/// Android Auto's browse tree and search, without the app's page: it uses the page's client once
/// the app is logged in, and otherwise logs in with the saved login itself, so the car works
/// when LunaDrome was never opened on the phone. All the network and JSON work runs off the
/// main thread (which is also the app's UI thread) and gives up after a timeout, so a slow
/// server shows an empty list instead of a car screen that spins forever.
/// </summary>
/// <remarks>
/// Media ids: "" = root (tabs), "home" = Song mix + the album lists, "mix" = Song mix (the whole
/// library shuffled), "albums:&lt;type&gt;" = an album list (recent/newest/frequent),
/// "playlists", "album:&lt;id&gt;" / "playlist:&lt;id&gt;" = a track list, "song|&lt;list id&gt;|&lt;index&gt;" =
/// a track in that list (playing it queues the whole list, like clicking a track in the app).
/// </remarks>
internal static class AutoLibrary
{
    // A long playlist as one browse result can be too big for Android's binder (and the car only
    // shows so many rows anyway). Playing a row still queues the whole list.
    private const int MaxRows = 300;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(12);

    /// <summary>Song mix: the whole library, shuffled (Symfonium's "shuffle all").</summary>
    public const string SongMixId = "mix";

    private static readonly ConcurrentDictionary<string, List<Song>> s_lists = new();
    private static readonly object s_loginLock = new();
    private static NavidromeClient? s_ownClient;
    private static Task<NavidromeClient?>? s_login;

    /// <summary>Set by the page once it's logged in (null when logged out).</summary>
    public static NavidromeClient? PageClient { get; set; }

    /// <summary>The logged-in client, or null when there's no saved login (or the server can't be
    /// reached).</summary>
    public static async Task<NavidromeClient?> GetClientAsync()
    {
        if (PageClient?.IsAuthenticated == true)
            return PageClient;
        if (s_ownClient?.IsAuthenticated == true)
            return s_ownClient;

        Task<NavidromeClient?> login;
        lock (s_loginLock)
            login = s_login ??= Task.Run(LoginWithSavedSettingsAsync);
        var client = await login;
        if (client == null)
            lock (s_loginLock)
                s_login = null; // try again next time (the phone may be online by then)
        return client;
    }

    private static async Task<NavidromeClient?> LoginWithSavedSettingsAsync()
    {
        try
        {
            var saved = SettingsService.Load();
            var password = CredentialProtector.Unprotect(saved.ProtectedPassword);
            if (string.IsNullOrWhiteSpace(saved.ServerUrl) || string.IsNullOrEmpty(password))
                return null;
            var client = new NavidromeClient { StreamQuality = PreferencesService.Load().StreamQuality };
            client.Configure(saved.ServerUrl, saved.Username, password);
            var (ok, error) = await client.LoginAsync().WaitAsync(Timeout);
            if (!ok)
            {
                AndroidLog.Write($"auto login: {error}");
                return null;
            }
            s_ownClient = client;
            return client;
        }
        catch (Exception ex)
        {
            AndroidLog.Write($"auto login: {ex.Message}");
            return null;
        }
    }

    public static Task<IReadOnlyList<BrowseItem>> BrowseAsync(string parentId) => Task.Run(async () =>
    {
        var client = await GetClientAsync();
        if (client == null)
            return (IReadOnlyList<BrowseItem>)new[] { new BrowseItem("none", "Log in to LunaDrome on your phone", "with \"Remember me\" on", null, false, false) };

        if (parentId == "")
        {
            s_lists.Clear(); // back at the top: pick up library changes
            // The car shows the root's rows as tabs (and nothing playable there), so Song mix
            // goes at the top of the first tab.
            return new[]
            {
                new BrowseItem("home", "Home", null, null, true, false),
                new BrowseItem("playlists", "Playlists", null, null, true, false),
            };
        }

        if (parentId == "home")
            return new[]
            {
                new BrowseItem(SongMixId, "Song mix", "Your whole library, shuffled", null, false, true),
                new BrowseItem("albums:recent", "Recently played", null, null, true, false),
                new BrowseItem("albums:newest", "Recently added", null, null, true, false),
                new BrowseItem("albums:frequent", "Most played", null, null, true, false),
            };

        if (parentId.StartsWith("albums:"))
        {
            var albums = await client.GetAlbumListAsync(parentId["albums:".Length..], 60).WaitAsync(Timeout);
            return albums.Select(a => new BrowseItem("album:" + a.Id, a.Name, a.ArtistName,
                AutoArtProvider.UriFor(a.Id), true, true)).ToList();
        }

        if (parentId == "playlists")
        {
            var playlists = await client.GetPlaylistsAsync().WaitAsync(Timeout);
            return playlists.Take(MaxRows).Select(p => new BrowseItem("playlist:" + p.Id, p.Name, $"{p.SongCount} tracks",
                AutoArtProvider.UriFor(p.CoverArtId), true, true)).ToList();
        }

        if (parentId.StartsWith("album:") || parentId.StartsWith("playlist:"))
        {
            var songs = await LoadListAsync(client, parentId);
            return songs.Take(MaxRows).Select((s, i) => new BrowseItem($"song|{parentId}|{i}", s.Title, s.ArtistName,
                null, false, true)).ToList();
        }

        return Array.Empty<BrowseItem>();
    });

    /// <summary>The track list and start index a playable media id stands for.</summary>
    public static Task<(List<Song> Songs, int Index)?> ResolveAsync(string mediaId) => Task.Run(async () =>
    {
        if (mediaId == SongMixId)
            return await SongMixAsync() is { Count: > 0 } mix ? (mix, 0) : ((List<Song>, int)?)null;
        var listId = mediaId;
        var index = 0;
        if (mediaId.StartsWith("song|"))
        {
            var parts = mediaId.Split('|');
            if (parts.Length != 3 || !int.TryParse(parts[2], out index))
                return ((List<Song>, int)?)null;
            listId = parts[1];
        }
        if (!listId.StartsWith("album:") && !listId.StartsWith("playlist:"))
            return null;
        var client = await GetClientAsync();
        if (client == null)
            return null;
        var songs = await LoadListAsync(client, listId);
        return index >= 0 && index < songs.Count ? (songs, index) : null;
    });

    /// <summary>"Hey Google, play &lt;query&gt; on LunaDrome": an exact album name wins, otherwise the
    /// matching songs. An empty query means "just play something" (Song mix).</summary>
    public static Task<List<Song>> SearchAsync(string query) => Task.Run(async () =>
    {
        var client = await GetClientAsync();
        if (client == null)
            return new List<Song>();
        if (string.IsNullOrWhiteSpace(query))
            return await SongMixAsync();
        var (_, albums, songs) = await client.SearchAsync(query, artistCount: 0, albumCount: 1, songCount: 30).WaitAsync(Timeout);
        if (albums.Count > 0 && (songs.Count == 0 || string.Equals(albums[0].Name, query.Trim(), StringComparison.OrdinalIgnoreCase)))
            return await client.GetSongsForAlbumAsync(albums[0].Id).WaitAsync(Timeout);
        return songs;
    });

    /// <summary>Every song in the library, in random order (empty when it can't be loaded).</summary>
    private static async Task<List<Song>> SongMixAsync()
    {
        var client = await GetClientAsync();
        if (client == null)
            return new List<Song>();
        // A few pages of 500 for a big library, so a longer wait than one list.
        var songs = await client.GetAllSongsAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(songs));
        return songs;
    }

    private static async Task<List<Song>> LoadListAsync(NavidromeClient client, string listId)
    {
        if (s_lists.TryGetValue(listId, out var cached))
            return cached;
        var songs = listId.StartsWith("album:")
            ? await client.GetSongsForAlbumAsync(listId["album:".Length..]).WaitAsync(Timeout)
            : await client.GetPlaylistSongsAsync(listId["playlist:".Length..]).WaitAsync(Timeout);
        s_lists[listId] = songs;
        return songs;
    }
}
