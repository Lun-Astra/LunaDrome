using LunaDrome.Controls;
using LunaDrome.Core.Services;
using LunaDrome.Services;

namespace LunaDrome;

// Which sound card / audio device of this computer plays the music (desktop). Listed in the
// "Play on" menu under This computer, next to Sendspin and DLNA. The choice is kept in the
// preferences and given to every player (the crossfade's second one too) before it plays.
public sealed partial class MainPage
{
    private IReadOnlyList<AudioDevices.Device> _audioDevices = [];
    private Task? _audioDeviceScan;

    // The output module the devices belong to; LunaDrome already uses these (see LocalAudioModules).
    private static string LocalAudioModule => OperatingSystem.IsWindows() ? "directsound" : "pulse";

    private Task ScanAudioDevicesAsync()
    {
        if (!AudioDevices.Supported)
            return Task.CompletedTask;
        if (_audioDeviceScan is { IsCompleted: false })
            return _audioDeviceScan;
        return _audioDeviceScan = Task.Run(() => AudioDevices.List(LocalAudioModule, _player, Log))
            .ContinueWith(t => _audioDevices = t.Result, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>The chosen device for a player. LibVLC can only move audio that's already
    /// playing, so this runs whenever a player starts playing (HookPlayer) - a new crossfade
    /// player, the first song, back from Sendspin - and right away when a device is picked.</summary>
    private void ApplyAudioDevice(Windows.Media.Playback.MediaPlayer player)
    {
        if (!AudioDevices.Supported || _sendspinActive || string.IsNullOrEmpty(_prefs.AudioDeviceId))
            return;
        if (AudioDevices.TrySet(player, _prefs.AudioDeviceId)
            || player.PlaybackSession.PlaybackState != Windows.Media.Playback.MediaPlaybackState.Playing)
            return;
        // "Playing" can come just before VLC's output exists: try once more a moment later.
        var id = _prefs.AudioDeviceId;
        var retry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        retry.Tick += (_, _) =>
        {
            retry.Stop();
            if (id == _prefs.AudioDeviceId && !_sendspinActive && !AudioDevices.TrySet(player, id))
                Log($"audio: couldn't switch the player to '{_prefs.AudioDeviceName}'");
        };
        retry.Start();
    }

    private void SelectAudioDevice(AudioDevices.Device? device)
    {
        var changed = (device?.Id ?? "") != _prefs.AudioDeviceId;
        _prefs.AudioDeviceId = device?.Id ?? "";
        _prefs.AudioDeviceName = device?.Name ?? "";
        PreferencesService.Save(_prefs);

        // Back from speakers / a DLNA renderer first, then the device.
        PlayOnThisDeviceFromDlna();
        _ = PlayOnThisComputerAsync();
        if (changed)
        {
            var players = new[] { _player, _fadingOut, _sparePlayer }.OfType<Windows.Media.Playback.MediaPlayer>().Distinct().ToList();
            if (device != null)
            {
                foreach (var player in players)
                    ApplyAudioDevice(player);
            }
            else
            {
                // LibVLC has no id for "the default": a fresh output starts on it, so reset the
                // outputs and reload the song where it is (like the stall recovery does).
                foreach (var player in players)
                    VlcAudioOutput.TrySet(player, LocalAudioModule);
                if (_isPlaying && !JamOwnsPlayback && _loadedEntry is { } entry && entry == _current)
                    PlayEntry(entry, _player.PlaybackSession.Position);
            }
            Log($"audio: playing on {device?.Name ?? "the default device"}");
        }
        UpdateOutputButton();
        ShowToast($"Playing on {device?.Name ?? $"{ThisDeviceName.ToLowerInvariant()} (default device)"}");
    }

    /// <summary>The "This computer" part of the Play on menu: the default device, then each one.</summary>
    private void AddLocalOutputItems(MenuFlyout flyout)
    {
        var local = !_sendspinActive && !DlnaActive;
        var chosen = _prefs.AudioDeviceId;

        var thisComputer = new ToggleMenuFlyoutItem
        {
            Text = _audioDevices.Count > 0 ? $"{ThisDeviceName} (default device)" : ThisDeviceName,
            IsChecked = local && chosen == "",
        };
        RichMenu.OnClick(thisComputer, () => SelectAudioDevice(null));
        RichMenu.SetIcon(thisComputer, OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() ? MenuIcons.Phone : MenuIcons.Computer);
        flyout.Items.Add(thisComputer);

        foreach (var device in _audioDevices)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = device.Name,
                IsChecked = local && device.Id == chosen,
                Padding = new Thickness(28, 6, 12, 6),   // under This computer
            };
            ToolTipService.SetToolTip(item, $"Audio device on {ThisDeviceName.ToLowerInvariant()}");
            RichMenu.OnClick(item, () => SelectAudioDevice(device));
            RichMenu.SetIcon(item, MenuIcons.Output);
            flyout.Items.Add(item);
        }

        // The saved device isn't there (unplugged): say so instead of silently using another.
        if (chosen != "" && _audioDeviceScan is { IsCompleted: true } && _audioDevices.All(d => d.Id != chosen))
            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = $"{_prefs.AudioDeviceName} (not connected)", IsEnabled = false, Padding = new Thickness(28, 6, 12, 6)
            });
    }
}
