using LunaDrome.Controls;
using LunaDrome.Core.Models;
using Windows.ApplicationModel.DataTransfer;

namespace LunaDrome;

// Renaming playlists and changing their order. Subsonic has no "move", so a new order is saved
// by sending the whole song list (NavidromeClient.SetPlaylistSongsAsync). Reordering only makes
// sense on the playlist's own order: with a sort or a search active, drag and the Move items
// are off.
public sealed partial class MainPage
{
    private Song? _draggedPlaylistSong;

    private bool CanReorderPlaylist
    {
        get
        {
            var view = _trackViews["PlaylistTracks"];
            return _currentPlaylist != null && view.SortKey == null && string.IsNullOrWhiteSpace(view.Search.Text);
        }
    }

    /// <summary>Called whenever the playlist view is refreshed (load, sort, search).</summary>
    private void UpdatePlaylistReorder()
    {
        var can = CanReorderPlaylist;
        PlaylistSongsList.CanDragItems = can;
        PlaylistSongsList.AllowDrop = can;
    }

    private void PlaylistSongsList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        _draggedPlaylistSong = CanReorderPlaylist ? e.Items.OfType<Song>().FirstOrDefault() : null;
        if (_draggedPlaylistSong == null)
        {
            e.Cancel = true;
            return;
        }
        e.Data.SetText(_draggedPlaylistSong.Title);
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void PlaylistSongsList_DragOver(object sender, DragEventArgs e)
    {
        if (_draggedPlaylistSong == null)
            return;
        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.Caption = "Move here";
    }

    private async void PlaylistSongsList_Drop(object sender, DragEventArgs e)
    {
        if (_draggedPlaylistSong is not { } song)
            return;
        _draggedPlaylistSong = null;

        // Insert before the first row whose middle is below the drop point.
        var songs = _trackViews["PlaylistTracks"].Raw;
        var target = songs.Count;
        for (var i = 0; i < songs.Count; i++)
        {
            if (PlaylistSongsList.ContainerFromIndex(i) is not FrameworkElement row)
                continue;
            if (e.GetPosition(row).Y < row.ActualHeight / 2)
            {
                target = i;
                break;
            }
        }
        var from = songs.IndexOf(song);
        await MovePlaylistSongAsync(from, target > from ? target - 1 : target);
    }

    private void PlaylistSongsList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) =>
        _draggedPlaylistSong = null;

    /// <summary>Move items for the playlist row menu (also how it works on a phone, where a hold
    /// starts selecting instead of dragging).</summary>
    private void AddPlaylistMoveItems(MenuFlyout flyout, Song song)
    {
        if (!CanReorderPlaylist)
            return;
        var songs = _trackViews["PlaylistTracks"].Raw;
        var index = songs.IndexOf(song);
        if (index < 0 || songs.Count < 2)
            return;

        var move = new MenuFlyoutSubItem { Text = "Move" };
        void Add(string text, int to)
        {
            var item = new MenuFlyoutItem { Text = text };
            RichMenu.OnClick(item, async () => await MovePlaylistSongAsync(index, to));
            move.Items.Add(item);
        }
        if (index > 0)
        {
            Add("To the top", 0);
            Add("Up", index - 1);
        }
        if (index < songs.Count - 1)
        {
            Add("Down", index + 1);
            Add("To the bottom", songs.Count - 1);
        }
        flyout.Items.Add(move);
    }

    private async Task MovePlaylistSongAsync(int from, int to)
    {
        var view = _trackViews["PlaylistTracks"];
        if (_currentPlaylist is not { } playlist || from < 0 || from >= view.Raw.Count || from == to)
            return;

        // Show the new order right away, then save it; on failure reload what the server has.
        var songs = view.Raw.ToList();
        var song = songs[from];
        songs.RemoveAt(from);
        songs.Insert(Math.Clamp(to, 0, songs.Count), song);
        view.Raw = songs;
        RefreshTrackView("PlaylistTracks");
        PlaylistSongsList.SelectedItem = song;

        try
        {
            await _client.SetPlaylistSongsAsync(playlist.Id, songs.Select(s => s.Id));
        }
        catch (Exception ex)
        {
            await ShowErrorAsync($"Couldn't save the new order: {ex.Message}");
        }
        await ReloadPlaylistSongsAsync(playlist);
    }

    // Fresh positions from the server (PlaylistIndex, needed to remove songs by position).
    private async Task ReloadPlaylistSongsAsync(Playlist playlist)
    {
        try
        {
            var songs = await _client.GetPlaylistSongsAsync(playlist.Id);
            if (_currentPlaylist?.Id != playlist.Id)
                return;
            var selectedId = (PlaylistSongsList.SelectedItem as Song)?.Id;
            PopulateSongArt(songs);
            _trackViews["PlaylistTracks"].Raw = songs;
            RefreshTrackView("PlaylistTracks");
            if (selectedId != null)
                PlaylistSongsList.SelectedItem = (PlaylistSongsList.ItemsSource as List<Song>)?.FirstOrDefault(s => s.Id == selectedId);
        }
        catch { /* the list keeps what it shows */ }
    }

    private void PlaylistRenameButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlaylist is { } playlist)
            _ = RenamePlaylistAsync(playlist);
    }

    private async Task RenamePlaylistAsync(Playlist playlist)
    {
        var name = await PromptForPlaylistNameAsync("Rename playlist", "Rename", playlist.Name);
        if (string.IsNullOrWhiteSpace(name) || name == playlist.Name)
            return;
        try
        {
            await _client.RenamePlaylistAsync(playlist.Id, name);
            playlist.Name = name;
            if (_currentPlaylist?.Id == playlist.Id)
                PlaylistTracksHeader.Text = name.ToUpperInvariant();
            await LoadPlaylistsAsync();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync($"Couldn't rename the playlist: {ex.Message}");
        }
    }
}
