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
/// Media ids: "" = root (tabs: home, artists, playlists, favorites), "home" = the mixes and album
/// shelves, "mix" / "favmix" / "artistmix:&lt;id&gt;" = shuffled library / favorites / artist,
/// "artist:&lt;id&gt;" = an artist's albums, "album:&lt;id&gt;" / "playlist:&lt;id&gt;" / "favorites" /
/// "search:&lt;query&gt;" = a track list, "song|&lt;list id&gt;|&lt;index&gt;" = a track in that list
/// (playing it queues the whole list, like clicking a track in the app).
/// </remarks>
internal static class AutoLibrary
{
    // A long playlist as one browse result can be too big for Android's binder (and the car only
    // shows so many rows anyway). Playing a row still queues the whole list.
    private const int MaxRows = 300;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(12);

    /// <summary>Song mix: the whole library, shuffled (Symfonium's "shuffle all").</summary>
    public const string SongMixId = "mix";
    /// <summary>The favorited songs, shuffled.</summary>
    public const string FavoritesMixId = "favmix";

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

    // How the car draws a row (Android Auto's content style hints).
    private const int ListRow = 1, GridTile = 2;
    // Rows on Home per album shelf.
    private const int ShelfSize = 12;

    /// <summary>An icon from the app's drawables, which the car can load directly.</summary>
    private static string Icon(string drawable) =>
        $"android.resource://{Android.App.Application.Context.PackageName}/drawable/{drawable}";

    public static Task<IReadOnlyList<BrowseItem>> BrowseAsync(string parentId) => Task.Run(async () =>
    {
        var client = await GetClientAsync();
        if (client == null)
            return (IReadOnlyList<BrowseItem>)new[] { new BrowseItem("none", "Log in to LunaDrome on your phone", "with \"Remember me\" on", null, false, false) };

        switch (parentId)
        {
            case "":
                s_lists.Clear(); // back at the top: pick up library changes
                // The car shows the root's rows as tabs (up to four, nothing playable there).
                return new[]
                {
                    new BrowseItem("home", "Home", null, Icon("lunadrome_tab_home"), true, false),
                    new BrowseItem("artists", "Artists", null, Icon("lunadrome_tab_artists"), true, false),
                    new BrowseItem("playlists", "Playlists", null, Icon("lunadrome_tab_playlists"), true, false),
                    new BrowseItem("favorites", "Favorites", null, Icon("lunadrome_heart"), true, false),
                };

            case "home":
            {
                // One page with shelves (group titles) instead of a folder per album list.
                var items = new List<BrowseItem>
                {
                    new(SongMixId, "Song mix", "Your whole library, shuffled", Icon("lunadrome_shuffle"), false, true, "Start listening", GridTile),
                    new(FavoritesMixId, "Favorites mix", "Your favorites, shuffled", Icon("lunadrome_heart_filled"), false, true, "Start listening", GridTile),
                };
                var shelves = new[] { ("recent", "Recently played"), ("newest", "Recently added"), ("frequent", "Most played") };
                var lists = await Task.WhenAll(shelves.Select(shelf => SafeAsync(client.GetAlbumListAsync(shelf.Item1, ShelfSize))));
                for (var i = 0; i < shelves.Length; i++)
                    items.AddRange(lists[i].Select(a => AlbumItem(a, shelves[i].Item2)));
                return items;
            }

            case "artists":
            {
                var artists = await client.GetArtistsAsync().WaitAsync(Timeout);
                return artists.Take(MaxRows).Select(a => new BrowseItem("artist:" + a.Id, a.Name,
                    a.AlbumCount == 1 ? "1 album" : $"{a.AlbumCount} albums", AutoArtProvider.UriFor("ar-" + a.Id), true, false, null, GridTile)).ToList();
            }

            case "playlists":
            {
                var playlists = await client.GetPlaylistsAsync().WaitAsync(Timeout);
                return playlists.Take(MaxRows).Select(p => new BrowseItem("playlist:" + p.Id, p.Name, $"{p.SongCount} tracks",
                    AutoArtProvider.UriFor(p.CoverArtId), true, true, null, GridTile)).ToList();
            }

            case "favorites":
            {
                var songs = await LoadListAsync(client, "favorites");
                if (songs.Count == 0)
                    return new[] { new BrowseItem("none", "No favorites yet", "Tap the heart while a song plays", null, false, false) };
                var items = new List<BrowseItem> { new(FavoritesMixId, "Shuffle favorites", $"{songs.Count} songs", Icon("lunadrome_shuffle"), false, true, null, ListRow) };
                items.AddRange(SongItems("favorites", songs, withArt: true));
                return items;
            }
        }

        if (parentId.StartsWith("artist:"))
        {
            var (artist, albums) = await client.GetArtistDetailAsync(parentId["artist:".Length..]).WaitAsync(Timeout);
            var items = new List<BrowseItem> { new("artistmix:" + artist.Id, $"Shuffle {artist.Name}", "All their songs", Icon("lunadrome_shuffle"), false, true, null, ListRow) };
            items.AddRange(albums.OrderByDescending(a => a.Year).Select(a => AlbumItem(a, "Albums")));
            return items;
        }

        // Older cars/app versions may still have the old album-list folders.
        if (parentId.StartsWith("albums:"))
        {
            var albums = await client.GetAlbumListAsync(parentId["albums:".Length..], 60).WaitAsync(Timeout);
            return albums.Select(a => AlbumItem(a, null)).ToList();
        }

        if (parentId.StartsWith("album:") || parentId.StartsWith("playlist:"))
        {
            var songs = await LoadListAsync(client, parentId);
            // An album's songs all have its cover; a playlist's each show their own.
            return SongItems(parentId, songs, withArt: parentId.StartsWith("playlist:")).ToList();
        }

        return Array.Empty<BrowseItem>();
    });

    /// <summary>The car's search screen: artists, albums and songs, typos forgiven like in the app.</summary>
    public static Task<IReadOnlyList<BrowseItem>> SearchItemsAsync(string query) => Task.Run(async () =>
    {
        var client = await GetClientAsync();
        if (client == null || string.IsNullOrWhiteSpace(query))
            return (IReadOnlyList<BrowseItem>)Array.Empty<BrowseItem>();
        var (artists, albums, songs) = await client.SearchAsync(query, artistCount: 5, albumCount: 10, songCount: 30).WaitAsync(Timeout);
        if (artists.Count + albums.Count + songs.Count == 0)
        {
            var fuzzy = await client.SearchFuzzyAsync(query, artistCount: 5, albumCount: 10, songCount: 30).WaitAsync(TimeSpan.FromSeconds(30));
            (artists, albums, songs) = (fuzzy.Artists, fuzzy.Albums, fuzzy.Songs);
        }
        var listId = "search:" + query.Trim();
        s_lists[listId] = songs;
        var items = new List<BrowseItem>();
        items.AddRange(artists.Select(a => new BrowseItem("artist:" + a.Id, a.Name, "Artist", AutoArtProvider.UriFor("ar-" + a.Id), true, false, "Artists", ListRow)));
        items.AddRange(albums.Select(a => AlbumItem(a, "Albums") with { Style = ListRow }));
        items.AddRange(SongItems(listId, songs, withArt: true).Select(i => i with { Group = "Songs" }));
        return items;
    });

    private static BrowseItem AlbumItem(Album a, string? group) =>
        new("album:" + a.Id, a.Name, a.Year > 0 ? $"{a.ArtistName} · {a.Year}" : a.ArtistName,
            AutoArtProvider.UriFor(a.Id), true, true, group, GridTile);

    private static IEnumerable<BrowseItem> SongItems(string listId, List<Song> songs, bool withArt) =>
        songs.Take(MaxRows).Select((s, i) => new BrowseItem($"song|{listId}|{i}", s.Title, s.ArtistName,
            withArt ? AutoArtProvider.UriFor(s.AlbumId) : null, false, true, null, ListRow));

    // A shelf that fails to load is left out instead of failing the whole page.
    private static async Task<List<Album>> SafeAsync(Task<List<Album>> load)
    {
        try { return await load.WaitAsync(Timeout); }
        catch (Exception ex)
        {
            AndroidLog.Write($"auto home shelf: {ex.Message}");
            return new();
        }
    }

    /// <summary>The track list and start index a playable media id stands for.</summary>
    public static Task<(List<Song> Songs, int Index)?> ResolveAsync(string mediaId) => Task.Run(async () =>
    {
        if (mediaId == SongMixId)
            return await SongMixAsync() is { Count: > 0 } mix ? (mix, 0) : ((List<Song>, int)?)null;
        if (mediaId == FavoritesMixId || mediaId.StartsWith("artistmix:"))
        {
            var mixClient = await GetClientAsync();
            if (mixClient == null)
                return null;
            var mixed = mediaId == FavoritesMixId
                ? new List<Song>(await LoadListAsync(mixClient, "favorites"))
                : await ArtistSongsAsync(mixClient, mediaId["artistmix:".Length..]);
            Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(mixed));
            return mixed.Count > 0 ? (mixed, 0) : null;
        }
        var listId = mediaId;
        var index = 0;
        if (mediaId.StartsWith("song|"))
        {
            var parts = mediaId.Split('|');
            if (parts.Length != 3 || !int.TryParse(parts[2], out index))
                return ((List<Song>, int)?)null;
            listId = parts[1];
        }
        if (!listId.StartsWith("album:") && !listId.StartsWith("playlist:") && listId != "favorites" && !listId.StartsWith("search:"))
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

    // Every song on the artist's albums.
    private static async Task<List<Song>> ArtistSongsAsync(NavidromeClient client, string artistId)
    {
        var albums = await client.GetAlbumsForArtistAsync(artistId).WaitAsync(Timeout);
        var lists = await Task.WhenAll(albums.Select(a => client.GetSongsForAlbumAsync(a.Id).WaitAsync(Timeout)));
        return lists.SelectMany(l => l).ToList();
    }

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
        // A search's songs are kept by SearchItemsAsync; if they're gone, search again.
        if (listId.StartsWith("search:"))
        {
            await SearchItemsAsync(listId["search:".Length..]);
            return s_lists.TryGetValue(listId, out var found) ? found : new();
        }
        var songs = listId == "favorites"
            ? await client.GetFavoriteSongsAsync().WaitAsync(Timeout)
            : listId.StartsWith("album:")
            ? await client.GetSongsForAlbumAsync(listId["album:".Length..]).WaitAsync(Timeout)
            : await client.GetPlaylistSongsAsync(listId["playlist:".Length..]).WaitAsync(Timeout);
        s_lists[listId] = songs;
        return songs;
    }
}
