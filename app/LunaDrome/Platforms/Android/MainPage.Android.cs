using LunaDrome.Core.Models;
using LunaDrome.Services;

namespace LunaDrome;

// Android Auto (#26): the browse tree the car shows and what its play/search/seek requests do.
// Media ids: "" = root, "albums:<type>" = an album list (recent/newest/frequent/random),
// "playlists", "album:<id>" / "playlist:<id>" = a track list, "song|<list id>|<index>" = a
// track in that list (playing it queues the whole list, like clicking a track in the app).
public sealed partial class MainPage
{
    private readonly Dictionary<string, List<Song>> _autoTrackLists = new();

    // Android's back button / gesture: one step back (player, menu, queue, previous page). With
    // nothing left it sends the app to the background like the home button, so playback keeps
    // going, instead of closing it.
    private void ConnectBackButton()
    {
        Windows.UI.Core.SystemNavigationManager.GetForCurrentView().BackRequested += (_, e) =>
        {
            e.Handled = true;
            Controls.SwipeToQueue.Trace("BACK");
            if (!HandleBack())
                (Uno.UI.ContextHelper.Current as Android.App.Activity)?.MoveTaskToBack(true);
        };
    }

    private void ConnectAndroidAuto()
    {
        AndroidAutoBridge.Browse = BrowseForAutoAsync;
        AndroidAutoBridge.PlayFromMediaId = id => DispatcherQueue.TryEnqueue(async () => await PlayFromAutoAsync(id));
        AndroidAutoBridge.PlayFromSearch = query => DispatcherQueue.TryEnqueue(async () => await PlayFromAutoSearchAsync(query));
        AndroidAutoBridge.Seek = position => DispatcherQueue.TryEnqueue(() => SeekFromAuto(position));
    }

    private async Task<IReadOnlyList<BrowseItem>> BrowseForAutoAsync(string parentId)
    {
        if (!_client.IsAuthenticated)
            return new[] { new BrowseItem("none", "Log in to LunaDrome on your phone", null, null, false, false) };

        if (parentId == "")
        {
            _autoTrackLists.Clear(); // back at the top: pick up library changes
            return new[]
            {
                new BrowseItem("albums:recent", "Recently played", null, null, true, false),
                new BrowseItem("albums:newest", "Recently added", null, null, true, false),
                new BrowseItem("albums:frequent", "Most played", null, null, true, false),
                new BrowseItem("playlists", "Playlists", null, null, true, false),
            };
        }

        if (parentId.StartsWith("albums:"))
        {
            var albums = await _client.GetAlbumListAsync(parentId["albums:".Length..], 60);
            return albums.Select(a => new BrowseItem("album:" + a.Id, a.Name, a.ArtistName,
                _client.GetCoverArtUrl(a.Id, 300), true, true)).ToList();
        }

        if (parentId == "playlists")
        {
            var playlists = await _client.GetPlaylistsAsync();
            return playlists.Select(p => new BrowseItem("playlist:" + p.Id, p.Name, $"{p.SongCount} tracks",
                p.CoverArtUrl, true, true)).ToList();
        }

        if (parentId.StartsWith("album:") || parentId.StartsWith("playlist:"))
        {
            var songs = await LoadAutoTrackListAsync(parentId);
            return songs.Select((s, i) => new BrowseItem($"song|{parentId}|{i}", s.Title, s.ArtistName,
                null, false, true)).ToList();
        }

        return Array.Empty<BrowseItem>();
    }

    private async Task<List<Song>> LoadAutoTrackListAsync(string listId)
    {
        if (_autoTrackLists.TryGetValue(listId, out var cached))
            return cached;
        var songs = listId.StartsWith("album:")
            ? await _client.GetSongsForAlbumAsync(listId["album:".Length..])
            : await _client.GetPlaylistSongsAsync(listId["playlist:".Length..]);
        _autoTrackLists[listId] = songs;
        return songs;
    }

    private async Task PlayFromAutoAsync(string mediaId)
    {
        try
        {
            string listId = mediaId;
            int index = 0;
            if (mediaId.StartsWith("song|"))
            {
                var parts = mediaId.Split('|');
                if (parts.Length != 3 || !int.TryParse(parts[2], out index))
                    return;
                listId = parts[1];
            }
            if (!listId.StartsWith("album:") && !listId.StartsWith("playlist:"))
                return;
            var songs = await LoadAutoTrackListAsync(listId);
            PlayQueueFrom(songs, index);
        }
        catch (Exception ex)
        {
            Log($"android auto play {mediaId}: {ex.Message}");
        }
    }

    // "Hey Google, play <query> on LunaDrome". An empty query means "just play something".
    private async Task PlayFromAutoSearchAsync(string query)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                if (_current != null)
                {
                    Play();
                    return;
                }
                var random = await _client.GetAlbumListAsync("random", 1);
                if (random.Count > 0)
                    PlayQueueFrom(await _client.GetSongsForAlbumAsync(random[0].Id), 0);
                return;
            }

            var (_, albums, songs) = await _client.SearchAsync(query, artistCount: 0, albumCount: 1, songCount: 30);
            // An exact album-name hit wins ("play Persona 5"), otherwise the matching tracks.
            if (albums.Count > 0 && string.Equals(albums[0].Name, query.Trim(), StringComparison.OrdinalIgnoreCase))
                PlayQueueFrom(await _client.GetSongsForAlbumAsync(albums[0].Id), 0);
            else if (songs.Count > 0)
                PlayQueueFrom(songs, 0);
            else if (albums.Count > 0)
                PlayQueueFrom(await _client.GetSongsForAlbumAsync(albums[0].Id), 0);
        }
        catch (Exception ex)
        {
            Log($"android auto search '{query}': {ex.Message}");
        }
    }

    private void SeekFromAuto(TimeSpan position)
    {
        if (JamOwnsPlayback || _current == null)
            return;
        if (_loadedEntry != _current)
        {
            _pendingSeek = position;
            return;
        }
        _player.PlaybackSession.Position = position;
    }
}
