using LunaDrome.Core.Models;
using LunaDrome.Services;

namespace LunaDrome;

// Android Auto (#26): what the car's play/search/seek requests do while the app runs. The browse
// tree is in AutoLibrary (it also works without the app), the media ids are explained there.
public sealed partial class MainPage
{
    // Android's back button / gesture: one step back (player, menu, queue, previous page). With
    // nothing left it sends the app to the background like the home button, so playback keeps
    // going, instead of closing it.
    private void ConnectBackButton()
    {
        Droid.MainActivity.MenuOpen = () => XamlRoot != null && VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Count > 0;
        Droid.MainActivity.CloseMenus = () => DispatcherQueue.TryEnqueue(() =>
        {
            if (XamlRoot != null)
                foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot))
                    popup.IsOpen = false;
        });
        Windows.UI.Core.SystemNavigationManager.GetForCurrentView().BackRequested += (_, e) =>
        {
            e.Handled = true;
            Controls.SwipeToQueue.Trace("BACK");
            if (!HandleBack())
                (Uno.UI.ContextHelper.Current as Android.App.Activity)?.MoveTaskToBack(true);
        };
    }

    // Called when the media controls are set up and again after login; only a logged-in page
    // takes over from AutoPlayer.
    private void ConnectAndroidAuto()
    {
        if (!_client.IsAuthenticated)
            return;
        AutoLibrary.PageClient = _client;
        AndroidAutoBridge.PlayFromMediaId = id => DispatcherQueue.TryEnqueue(async () => await PlayFromAutoAsync(id));
        AndroidAutoBridge.PlayFromSearch = query => DispatcherQueue.TryEnqueue(async () => await PlayFromAutoSearchAsync(query));
        AndroidAutoBridge.Seek = position => DispatcherQueue.TryEnqueue(() => SeekFromAuto(position));
        TakeOverFromAutoPlayer();
    }

    // Android Auto started LunaDrome without the app and has been playing on its own: carry on
    // with that queue, at the same spot, in the app (before the saved queue is restored).
    private void TakeOverFromAutoPlayer()
    {
        if (AutoPlayer.TakeOver() is not { } handover)
            return;
        var entries = handover.Songs.Select(s => new QueueEntry(s)).ToList();
        PopulateSongArt(handover.Songs);
        _isShuffled = false;
        UpdateShuffleIcon();
        _unshuffledOrder = null;
        ReplaceQueue(entries);
        var entry = entries[Math.Clamp(handover.Index, 0, entries.Count - 1)];
        if (handover.Playing)
        {
            PlayEntry(entry, handover.Position);
            return;
        }
        SetCurrent(entry);
        SetPlaying(false);
        _pendingSeek = handover.Position;
        ProgressSlider.Maximum = Math.Max(1, entry.Song.DurationSeconds);
        ProgressSlider.Value = Math.Min(_pendingSeek.TotalSeconds, ProgressSlider.Maximum);
        ElapsedText.Text = FormatTime(_pendingSeek);
        DurationText.Text = FormatTime(TimeSpan.FromSeconds(entry.Song.DurationSeconds));
    }

    private async Task PlayFromAutoAsync(string mediaId)
    {
        try
        {
            if (await AutoLibrary.ResolveAsync(mediaId) is { } target)
                PlayQueueFrom(target.Songs, target.Index);
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
            if (string.IsNullOrWhiteSpace(query) && _current != null)
            {
                Play();
                return;
            }
            var songs = await AutoLibrary.SearchAsync(query);
            if (songs.Count > 0)
                PlayQueueFrom(songs, 0);
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
