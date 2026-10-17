using LunaDrome.Core.Sendspin;
using LunaDrome.Core.Services;
using Microsoft.UI.Xaml.Media;
using LunaDrome.Services;

namespace LunaDrome;

// Output picker: play on this computer or on Sendspin speakers (Music Assistant's synced
// multi-room protocol). LunaDrome is then the Sendspin server: the players keep running as
// usual, SendspinTap takes their decoded audio instead of the sound card, and SendspinOutput
// streams it to the chosen speakers, which all play it in sync. Desktop only.
public sealed partial class MainPage
{
    private static readonly TimeSpan SendspinRescanAfter = TimeSpan.FromSeconds(10);

    private SendspinOutput? _sendspin;
    private readonly SendspinMediaControls _sendspinControls = new();
    private readonly Dictionary<Windows.Media.Playback.MediaPlayer, SendspinSource> _sendspinSources = new();
    private IReadOnlyList<SendspinSpeakerInfo> _sendspinFound = [];
    private readonly HashSet<string> _sendspinConnecting = new();
    private DateTime _sendspinLastScan = DateTime.MinValue;
    private Task? _sendspinScan;
    private bool _sendspinActive;
    private string? _sendspinStatus;

    private static bool SendspinSupported => !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS();

    private void InitSendspin()
    {
        OutputButton.Visibility = SendspinSupported ? Visibility.Visible : Visibility.Collapsed;
        _sendspinControls.CommandReceived += command => DispatcherQueue.TryEnqueue(() => HandleMediaCommand(command));
        if (SendspinSupported)
            _ = ScanSendspinAsync();   // so the list is ready when the picker opens
    }

    private SendspinOutput EnsureSendspinOutput()
    {
        if (_sendspin != null)
            return _sendspin;
        if (string.IsNullOrEmpty(_prefs.SendspinServerId))
        {
            _prefs.SendspinServerId = Guid.NewGuid().ToString("N");
            PreferencesService.Save(_prefs);
        }
        _sendspin = new SendspinOutput(_prefs.SendspinServerId, $"LunaDrome ({Environment.MachineName})") { Log = Log };
        _sendspin.SpeakerLost += (speaker, reason) => DispatcherQueue.TryEnqueue(() => OnSendspinSpeakerLost(speaker, reason));
        _sendspin.SpeakersChanged += () => DispatcherQueue.TryEnqueue(UpdateOutputButton);
        return _sendspin;
    }

    private async Task ScanSendspinAsync()
    {
        if (_sendspinScan is { IsCompleted: false })
        {
            await _sendspinScan;
            return;
        }
        _sendspinLastScan = DateTime.UtcNow;
        _sendspinScan = Task.Run(async () =>
        {
            try
            {
                var found = await SendspinDiscovery.BrowseAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
                Log($"sendspin: found {found.Count} speaker(s): {string.Join(", ", found.Select(f => $"{f.Name} {f.Url}"))}");
                _sendspinFound = found;
            }
            catch (Exception ex)
            {
                Log($"sendspin: discovery failed: {ex.Message}");
            }
        });
        await _sendspinScan;
    }

    // ---- the picker ----

    private async void OutputButton_Click(object sender, RoutedEventArgs e)
    {
        var anchor = (FrameworkElement)sender;
        var flyout = new MenuFlyout();
        FillOutputFlyout(flyout);
        flyout.ShowAt(anchor);
        if (DateTime.UtcNow - _sendspinLastScan > SendspinRescanAfter || _sendspinScan is { IsCompleted: false })
        {
            await ScanSendspinAsync();
            FillOutputFlyout(flyout);   // the open menu picks up what the scan found
        }
    }

    private void FillOutputFlyout(MenuFlyout flyout)
    {
        flyout.Items.Clear();
        flyout.Items.Add(new MenuFlyoutItem { Text = "Play on", IsEnabled = false });

        var local = new ToggleMenuFlyoutItem { Text = "This computer", IsChecked = !_sendspinActive };
        local.Click += (_, _) => _ = PlayOnThisComputerAsync();
        flyout.Items.Add(local);

        var connected = _sendspin?.Speakers ?? [];
        var entries = _sendspinFound.Concat(ManualSendspinSpeakers().Where(m => _sendspinFound.All(f => f.Key != m.Key)))
            .Select(f => (f.Name, f.Key, Info: (SendspinSpeakerInfo?)f)).ToList();
        // Connected speakers stay listed even when a scan missed them.
        foreach (var s in connected.Where(s => entries.All(e => e.Key != s.Url.ToString())))
            entries.Add((s.Name, s.Url.ToString(), null));

        if (entries.Count > 0 || _sendspinScan is { IsCompleted: false })
            flyout.Items.Add(new MenuFlyoutSeparator());
        foreach (var (name, key, info) in entries)
        {
            var speaker = connected.FirstOrDefault(s => s.Url.ToString() == key);
            var connecting = _sendspinConnecting.Contains(key);
            var label = speaker?.Name ?? name;   // once connected, the name the speaker gives itself
            var item = new ToggleMenuFlyoutItem
            {
                Text = connecting ? $"{label} (connecting…)" : label,
                IsChecked = speaker != null,
                IsEnabled = !connecting,
            };
            ToolTipService.SetToolTip(item, speaker != null
                ? $"Sendspin speaker · {speaker.ProductName ?? speaker.Name}{(speaker.State is "" or "synchronized" ? "" : $" · {speaker.State}")}"
                : $"Sendspin speaker at {key}");
            item.Click += (_, _) =>
            {
                if (speaker != null) _ = RemoveSendspinSpeakerAsync(speaker);
                else if (info != null) _ = AddSendspinSpeakerAsync(info);
            };
            flyout.Items.Add(item);
        }
        if (_sendspinScan is { IsCompleted: false })
            flyout.Items.Add(new MenuFlyoutItem { Text = "Looking for speakers…", IsEnabled = false });
        else if (entries.Count == 0)
            flyout.Items.Add(new MenuFlyoutItem { Text = "No Sendspin speakers found", IsEnabled = false });
        // For networks where mDNS doesn't get through (VLANs, Docker): type the address.
        AddMenuItem(flyout, "Add a speaker by address…", () => _ = AddSendspinByAddressAsync());

        var status = _sendspinStatus ?? (_sendspinActive && _sendspin != null
            ? $"Speakers play {_sendspin.Latency.TotalSeconds:0.0} s behind this screen"
            : null);
        if (status != null)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(new MenuFlyoutItem { Text = status, IsEnabled = false });
        }
    }

    private void UpdateOutputButton()
    {
        var count = _sendspin?.Speakers.Count ?? 0;
        if (Application.Current.Resources.TryGetValue(_sendspinActive ? "TextPrimaryBrush" : "TextSecondaryBrush", out var brush)
            && brush is Brush stroke)
            OutputIcon.Stroke = stroke;
        ToolTipService.SetToolTip(OutputButton, count switch
        {
            0 => "Play on: this computer",
            1 => $"Play on: {_sendspin!.Speakers[0].Name}",
            _ => $"Play on: {count} speakers",
        });
    }

    /// <summary>Speakers added by address; they're listed like discovered ones.</summary>
    private IEnumerable<SendspinSpeakerInfo> ManualSendspinSpeakers() =>
        _prefs.SendspinManualSpeakers.Select(a => ParseSendspinAddress(a)).OfType<Uri>()
            .Select(u => new SendspinSpeakerInfo($"{u.Host}:{u.Port}", u));

    /// <summary>"host", "host:port" or a full ws:// URL; port 8928 and /sendspin by default.</summary>
    private static Uri? ParseSendspinAddress(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return null;
        if (!text.Contains("://"))
            text = "ws://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss"))
            return null;
        var builder = new UriBuilder(uri);
        if (uri.IsDefaultPort && !text.Contains($":{uri.Port}", StringComparison.Ordinal))
            builder.Port = 8928;
        if (builder.Path is "" or "/")
            builder.Path = "/sendspin";
        return builder.Uri;
    }

    private async Task AddSendspinByAddressAsync()
    {
        var box = new TextBox { PlaceholderText = "192.168.1.50 or 192.168.1.50:8928", MinWidth = 320 };
        var dialog = new ContentDialog
        {
            Title = "Add a Sendspin speaker",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "The speaker's address on your network (port 8928 unless it says otherwise).", TextWrapping = TextWrapping.Wrap },
                    box,
                },
            },
            PrimaryButtonText = "Add and play",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        ContentDialogResult result;
        try { result = await dialog.ShowAsync(); }
        catch { return; /* another dialog already open */ }
        if (result != ContentDialogResult.Primary)
            return;
        if (ParseSendspinAddress(box.Text) is not { } url)
        {
            ShowToast("That isn't a speaker address (e.g. 192.168.1.50:8928)");
            return;
        }
        if (!_prefs.SendspinManualSpeakers.Contains(url.ToString()))
        {
            _prefs.SendspinManualSpeakers.Add(url.ToString());
            PreferencesService.Save(_prefs);
        }
        await AddSendspinSpeakerAsync(new SendspinSpeakerInfo($"{url.Host}:{url.Port}", url));
    }

    // ---- switching ----

    private async Task AddSendspinSpeakerAsync(SendspinSpeakerInfo info)
    {
        var output = EnsureSendspinOutput();
        _sendspinConnecting.Add(info.Key);
        _sendspinStatus = null;
        Log($"sendspin: connecting to {info.Name} ({info.Url})");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var speaker = await output.AddSpeakerAsync(info.Url, timeout.Token);
            Log($"sendspin: connected to {speaker.Name} ({speaker.SoftwareVersion}), roles {string.Join(",", speaker.ActiveRoles)}");
            if (!_sendspinActive)
                RouteToSendspin(true);
            ShowToast($"Playing on {speaker.Name}");
        }
        catch (Exception ex)
        {
            Log($"sendspin: connecting to {info.Name} failed: {ex}");
            _sendspinStatus = $"Couldn't connect to {info.Name}: {(ex is OperationCanceledException ? "no answer" : ex.Message)}";
            ShowToast(_sendspinStatus);
        }
        finally
        {
            _sendspinConnecting.Remove(info.Key);
            UpdateOutputButton();
        }
    }

    private async Task RemoveSendspinSpeakerAsync(SendspinSpeaker speaker)
    {
        if (_sendspin == null)
            return;
        var last = _sendspin.Speakers.Count <= 1;
        if (last)
            RouteToSendspin(false);   // the music moves back here before the speaker stops
        await _sendspin.RemoveSpeakerAsync(speaker);
        Log($"sendspin: removed {speaker.Name}");
        UpdateOutputButton();
    }

    private async Task PlayOnThisComputerAsync()
    {
        if (!_sendspinActive || _sendspin == null)
            return;
        RouteToSendspin(false);
        foreach (var speaker in _sendspin.Speakers)
            await _sendspin.RemoveSpeakerAsync(speaker);
        UpdateOutputButton();
        ShowToast("Playing on this computer");
    }

    private void OnSendspinSpeakerLost(SendspinSpeaker speaker, string reason)
    {
        Log($"sendspin: lost {speaker.Name}: {reason}");
        var others = _sendspin?.Speakers.Count ?? 0;
        var why = reason switch
        {
            "another_server" => "another app took it over",
            "shutdown" => "it shut down",
            "user_request" => "it was disconnected on the speaker",
            "restart" => "it's restarting",
            _ => "connection lost",
        };
        if (others == 0 && _sendspinActive)
        {
            RouteToSendspin(false);
            ShowToast($"{speaker.Name} disconnected ({why}) - playing on this computer");
        }
        else
        {
            ShowToast($"{speaker.Name} disconnected ({why})");
        }
        UpdateOutputButton();
    }

    /// <summary>Points the players at Sendspin (or back at the sound card) and re-opens the
    /// current song where it was, since the output only changes when a song opens.</summary>
    private void RouteToSendspin(bool on)
    {
        if (_sendspinActive == on)
            return;
        var wasPlaying = _isPlaying;
        var loaded = _loadedEntry != null && _loadedEntry == _current && _endedEntry != _loadedEntry;
        var position = loaded ? _player.PlaybackSession.Position : _pendingSeek;
        FinishCrossfade();

        _sendspinActive = on;
        foreach (var player in new[] { _player, _sparePlayer }.OfType<Windows.Media.Playback.MediaPlayer>())
            ApplySendspinRoute(player);
        _sendspinControls.Output = on ? _sendspin : null;
        Log($"sendspin: output {(on ? "on" : "off")} (was {(wasPlaying ? "playing" : "paused")} at {position:mm\\:ss})");

        if (JamOwnsPlayback)
        {
            ShowToast(on ? "The speakers take over with the next jam song" : "This computer takes over with the next jam song");
        }
        else if (loaded && _current != null)
        {
            if (wasPlaying)
            {
                PlayEntry(_current, position);
            }
            else
            {
                // Paused: re-opened (with the new output) when play is pressed.
                try { _player.Source = null; } catch { /* reloaded on play anyway */ }
                _loadedEntry = null;
                _pendingSeek = position;
            }
        }
        UpdateOutputButton();
    }

    /// <summary>Gives a player the current route; also called for a newly made crossfade player.</summary>
    private void ApplySendspinRoute(Windows.Media.Playback.MediaPlayer player)
    {
        if (_sendspinActive && _sendspin != null)
        {
            if (!_sendspinSources.TryGetValue(player, out var source))
                _sendspinSources[player] = source = _sendspin.CreateSource();
            if (!SendspinTap.TryAttach(player, source))
                Log("sendspin: couldn't reach the VLC player to take its audio");
        }
        else if (SendspinTap.IsAttached(player))
        {
            SendspinTap.Detach(player);
            if (!LocalAudioModules.Any(module => VlcAudioOutput.TrySet(player, module)))
                Log("sendspin: couldn't switch the player back to the sound card");
        }
    }

    // Windows: DirectSound (a volume per player, see PrepareAudioOutput); Linux: PulseAudio
    // (PipeWire speaks it too), ALSA as the fallback.
    private static IEnumerable<string> LocalAudioModules =>
        OperatingSystem.IsWindows() ? ["directsound"] : ["pulse", "alsa"];

    private void DisposeSendspin()
    {
        _sendspinControls.Output = null;
        var output = _sendspin;
        _sendspin = null;
        if (output != null)
            // Tell the speakers the stream ended, briefly; the app is closing.
            output.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
    }
}
