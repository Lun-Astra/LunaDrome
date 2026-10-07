using LunaDrome.Controls;
using LunaDrome.Core.Models;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;

namespace LunaDrome;

// #30, Android: hold a song to start selecting, tap more songs, then act on all of them from the
// bar at the top (add to queue, the ⋯ menu: play, playlist, jam, remove). Works on the track
// lists (Tracks, playlists, search) and the queue lists; on desktop right-click keeps its menu.
public sealed partial class MainPage
{
    private ListView? _selectionList;
    private ListViewSelectionMode _selectionPreviousMode;

    private bool InSelectionMode => _selectionList != null;

    // A touch hold raises RightTapped; only on Android does it start selecting.
    private static bool IsTouchHold(RightTappedRoutedEventArgs e) =>
        OperatingSystem.IsAndroid() && e.PointerDeviceType == PointerDeviceType.Touch;

    private bool IsQueueList(ListView list) => list == QueueList || list == SideQueueList;

    private void EnterSelection(ListView list, object item)
    {
        if (_selectionList != null && _selectionList != list)
            ExitSelection();
        if (_selectionList == null)
        {
            _selectionList = list;
            _selectionPreviousMode = list.SelectionMode;
            list.SelectionMode = ListViewSelectionMode.Multiple;
            list.SelectedItems.Clear();
            list.SelectionChanged += Selection_Changed;
        }
        if (!list.SelectedItems.Contains(item))
            list.SelectedItems.Add(item);
        UpdateSelectionBar();
    }

    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_selectionList == null)
            return;
        if (_selectionList.SelectedItems.Count == 0)
            ExitSelection();   // deselected the last one
        else
            UpdateSelectionBar();
    }

    private void ExitSelection()
    {
        if (_selectionList is not { } list)
            return;
        _selectionList = null;
        list.SelectionChanged -= Selection_Changed;
        list.SelectedItems.Clear();
        list.SelectionMode = _selectionPreviousMode;
        SelectionBar.Visibility = Visibility.Collapsed;
    }

    private void UpdateSelectionBar()
    {
        if (_selectionList == null)
            return;
        var count = _selectionList.SelectedItems.Count;
        SelectionCountText.Text = count == 1 ? "1 selected" : $"{count} selected";
        // In the queue the songs are already queued: the ⋯ menu has the queue actions instead.
        SelectionAddToQueueButton.Visibility = IsQueueList(_selectionList) ? Visibility.Collapsed : Visibility.Visible;
        SelectionBar.Visibility = Visibility.Visible;
    }

    // The selected songs in list order (the order they were tapped doesn't matter).
    private List<Song> SelectedSongs()
    {
        if (_selectionList?.ItemsSource is not System.Collections.IEnumerable source)
            return new();
        var selected = _selectionList.SelectedItems.ToHashSet();
        return source.Cast<object>().Where(selected.Contains)
            .Select(o => o switch { Song s => s, QueueEntry q => q.Song, _ => null })
            .OfType<Song>().ToList();
    }

    private void SelectionClose_Click(object sender, RoutedEventArgs e) => ExitSelection();

    private void SelectionAddToQueue_Click(object sender, RoutedEventArgs e)
    {
        var songs = SelectedSongs();
        ExitSelection();
        if (songs.Count == 0)
            return;
        AddToQueue(songs);
        ShowToast(songs.Count == 1 ? $"Added to queue: {songs[0].Title}" : $"Added {songs.Count} songs to the queue");
    }

    private void SelectionMore_Click(object sender, RoutedEventArgs e)
    {
        if (_selectionList is not { } list || sender is not FrameworkElement anchor)
            return;

        MenuFlyout flyout;
        if (IsQueueList(list))
        {
            var entries = SelectedQueueEntries(list);
            flyout = BuildQueueMenu(list, entries, entries.Count == 1 ? entries[0] : null);
        }
        else
        {
            var songs = SelectedSongs();
            flyout = new MenuFlyout();
            AddMenuItem(flyout, "Play now", () => PlayQueueFrom(songs, 0));
            AddMenuItem(flyout, "Play next", () => QueueSongsNext(songs));
            AddMenuItem(flyout, "Add to queue", () => AddToQueue(songs));
            AddMenuItem(flyout, "Play last", () => QueueSongsLast(songs));
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(BuildAddToPlaylistSubmenu(songs));
            AddJamMenuItem(flyout, () => Task.FromResult(songs));
            if (songs.Count == 1)
                AddFavoriteToggle(flyout, songs[0]);
            if (list.Tag as string == "PlaylistTracks")
            {
                flyout.Items.Add(new MenuFlyoutSeparator());
                AddMenuItem(flyout, songs.Count == 1 ? "Remove from playlist" : $"Remove {songs.Count} songs from playlist",
                    async () => await RemoveFromPlaylistAsync(songs));
            }
        }
        flyout.Items.Add(new MenuFlyoutSeparator());
        AddMenuItem(flyout, "Select all", () =>
        {
            if (_selectionList is { } l)
            {
                l.SelectAll();
                UpdateSelectionBar();
            }
        });
        // Any action ends selecting ("Select all" keeps it going).
        foreach (var item in flyout.Items.OfType<MenuFlyoutItem>().Where(i => i.Text != "Select all"))
            item.Click += (_, _) => ExitSelection();
        flyout.ShowAt(anchor);
    }
}
