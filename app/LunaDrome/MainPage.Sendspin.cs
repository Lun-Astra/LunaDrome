using LunaDrome.Controls;
using LunaDrome.Core.Sendspin;
using LunaDrome.Core.Services;
using Microsoft.UI.Xaml.Input;
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
    private SendspinCredentials? _sendspinCredentials;
    private readonly SendspinMediaControls _sendspinControls = new();
    private readonly Dictionary<Windows.Media.Playback.MediaPlayer, SendspinSource> _sendspinSources = new();
    private IReadOnlyList<SendspinSpeakerInfo> _sendspinFound = [];
    private readonly HashSet<string> _sendspinConnecting = new();
    private DateTime _sendspinLastScan = DateTime.MinValue;
    private Task? _sendspinScan;
    private bool _sendspinActive;
    private string? _sendspinStatus;

    // Desktop: LibVLC hands over its audio. Phones: the song is decoded separately (SendspinFollower).
    private static bool SendspinSupported => true;

    private static string ThisDeviceName => OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() ? "This phone" : "This computer";

    private void InitSendspin()
    {
        OutputButton.Visibility = SendspinSupported ? Visibility.Visible : Visibility.Collapsed;
        _sendspinControls.CommandReceived += command => DispatcherQueue.TryEnqueue(() => HandleMediaCommand(command));
        if (SendspinSupported)
        {
            _ = ScanSendspinAsync();   // so the list is ready when the picker opens
            _ = ScanDlnaAsync();
            _ = ScanAudioDevicesAsync();
        }
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
        _sendspinCredentials = SendspinCredentialsFromPrefs();
        _sendspin = new SendspinOutput(_sendspinCredentials, $"LunaDrome ({Environment.MachineName})") { Log = Log };
        _sendspin.SpeakerLost += (speaker, reason) => DispatcherQueue.TryEnqueue(() => OnSendspinSpeakerLost(speaker, reason));
        _sendspin.SpeakersChanged += () => DispatcherQueue.TryEnqueue(UpdateOutputButton);
        return _sendspin;
    }

    /// <summary>The encrypted protocol's identity (made once, kept in the preferences,
    /// encrypted) and the pairing records.</summary>
    private SendspinCredentials SendspinCredentialsFromPrefs()
    {
        byte[]? key = null;
        if (!string.IsNullOrEmpty(_prefs.SendspinServerKey))
        {
            try { key = Convert.FromBase64String(CredentialProtector.Unprotect(_prefs.SendspinServerKey) ?? ""); }
            catch (Exception ex) { Log($"sendspin: stored server key unreadable, making a new one: {ex.Message}"); }
        }
        if (key is not { Length: 32 })
        {
            key = X25519Keys.NewPrivate();
            _prefs.SendspinServerKey = CredentialProtector.Protect(Convert.ToBase64String(key));
            PreferencesService.Save(_prefs);
        }
        return new SendspinCredentials(_prefs.SendspinServerId, key,
            clientId =>
            {
                if (!_prefs.SendspinPairings.TryGetValue(clientId, out var protectedPsk)) return null;
                try { return Convert.FromBase64String(CredentialProtector.Unprotect(protectedPsk) ?? ""); }
                catch { return null; }
            },
            (clientId, psk) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (psk == null) _prefs.SendspinPairings.Remove(clientId);
                    else _prefs.SendspinPairings[clientId] = CredentialProtector.Protect(Convert.ToBase64String(psk));
                    PreferencesService.Save(_prefs);
                });
            });
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
#if ANDROID
                // Android drops multicast (mDNS) unless the app holds a MulticastLock while it listens.
                var wifi = (Android.Net.Wifi.WifiManager?)Android.App.Application.Context.GetSystemService(Android.Content.Context.WifiService);
                var multicast = wifi?.CreateMulticastLock("LunaDrome Sendspin discovery");
                multicast?.Acquire();
                IReadOnlyList<SendspinSpeakerInfo> found;
                try { found = await SendspinDiscovery.BrowseAsync(TimeSpan.FromSeconds(3), CancellationToken.None); }
                finally { multicast?.Release(); }
#else
                var found = await SendspinDiscovery.BrowseAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
#endif
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

    private void OutputButton_Click(object sender, RoutedEventArgs e) => ShowOutputPicker((FrameworkElement)sender);

    /// <summary>The "Play on" menu, from the output button or (phones) a swipe down on the playback bar.</summary>
    private async void ShowOutputPicker(FrameworkElement anchor)
    {
        var flyout = new MenuFlyout();
        FillOutputFlyout(flyout);
        var menu = RichMenu.ShowAt(flyout, anchor);
        var devices = ScanAudioDevicesAsync();   // headphones plugged in since last time
        if (DateTime.UtcNow - _sendspinLastScan > SendspinRescanAfter || _sendspinScan is { IsCompleted: false })
            await Task.WhenAll(ScanSendspinAsync(), ScanDlnaAsync(), devices);
        else
            await devices;
        FillOutputFlyout(flyout);   // the open menu picks up what the scans found
        menu.Refresh();
    }

    private void FillOutputFlyout(MenuFlyout flyout)
    {
        flyout.Items.Clear();
        flyout.Items.Add(new MenuFlyoutItem { Text = "Play on", IsEnabled = false });

        AddLocalOutputItems(flyout);   // this computer: default device + each sound card (MainPage.AudioDevice.cs)

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
            RichMenu.SetIcon(item, MenuIcons.Speaker);
            ToolTipService.SetToolTip(item, speaker != null
                ? $"Sendspin speaker · {speaker.ProductName ?? speaker.Name}{(speaker.State is "" or "synchronized" ? "" : $" · {speaker.State}")}"
                : $"Sendspin speaker at {key}");
            RichMenu.OnClick(item, () =>
            {
                if (speaker != null) _ = RemoveSendspinSpeakerAsync(speaker);
                else if (info != null) _ = AddSendspinSpeakerAsync(info);
            });
            flyout.Items.Add(item);
        }
        if (_sendspinScan is { IsCompleted: false })
            flyout.Items.Add(new MenuFlyoutItem { Text = "Looking for speakers…", IsEnabled = false });
        else if (entries.Count == 0)
            flyout.Items.Add(new MenuFlyoutItem { Text = "No Sendspin speakers found", IsEnabled = false });
        // For networks where mDNS doesn't get through (VLANs, Docker): type the address.
        AddMenuItem(flyout, "Add a speaker by address…", () => _ = AddSendspinByAddressAsync());
        if (flyout.Items[^1] is MenuFlyoutItem byAddress)
            RichMenu.SetIcon(byAddress, MenuIcons.Plus);
        AddDlnaItems(flyout);

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
        if (Application.Current.Resources.TryGetValue(_sendspinActive || DlnaActive ? "TextPrimaryBrush" : "TextSecondaryBrush", out var brush)
            && brush is Brush stroke)
            OutputIcon.Stroke = stroke;
        ToolTipService.SetToolTip(OutputButton, _dlna != null ? $"Play on: {_dlna.Renderer.Name}" : count switch
        {
            0 when _prefs.AudioDeviceName != "" => $"Play on: {_prefs.AudioDeviceName}",
            0 => $"Play on: {ThisDeviceName.ToLowerInvariant()}",
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

    // ---- pairing (encrypted speakers that don't allow unpaired access) ----

    /// <summary>Pairs with a connected speaker that won't play unpaired, then adds it.
    /// Null when the user cancelled or pairing failed (already told why).</summary>
    private async Task<SendspinSpeaker?> PairSendspinAsync(SendspinSpeaker speaker)
    {
        var pairing = new SendspinPairing(speaker, _sendspinCredentials!);
        var dynamic = SendspinPairing.OffersDynamicCode(speaker);
        var codeMethod = dynamic || SendspinPairing.OffersStaticCode(speaker);
        var tokenMethod = SendspinPairing.OffersToken(speaker);
        string? status = null;
        pairing.Status += text => status = text;
        Log($"sendspin: {speaker.Name} needs pairing (methods {string.Join(",", speaker.PairMethods)}{(speaker.CredentialMismatch ? ", it lost the earlier pairing" : "")})");
        try
        {
            var useToken = !codeMethod;
            if (codeMethod)
            {
                try
                {
                    await pairing.PairWithCodeAsync(dynamic, retry => OnUiAsync(async () =>
                    {
                        var (code, wantsToken) = await AskPairingCodeAsync(speaker, dynamic, retry, tokenMethod, status);
                        useToken = wantsToken;
                        return code;
                    }), CancellationToken.None);
                }
                catch (SendspinPairingException ex) when (ex.Cancelled && useToken)
                {
                    // Switching to the token: the code attempt was cancelled for it.
                }
            }
            if (useToken)
            {
                if (!tokenMethod)
                    throw new SendspinPairingException($"{speaker.Name} can't be paired by token.");
                var token = await AskPairingTokenAsync(speaker);
                if (token == null)
                    throw new SendspinPairingException("Pairing cancelled.", cancelled: true);
                await pairing.PairWithTokenAsync(token, CancellationToken.None);
            }
            Log($"sendspin: paired with {speaker.Name}");
            await _sendspin!.AdmitAsync(speaker);
            ShowToast($"Paired with {speaker.Name}");
            return speaker;
        }
        catch (Exception ex) when (ex is SendspinPairingException or SendspinProtocolException or OperationCanceledException)
        {
            Log($"sendspin: pairing with {speaker.Name} ended: {ex.Message}");
            if (ex is not SendspinPairingException { Cancelled: true })
                ShowToast(ex.Message);
            await speaker.DisposeAsync();
            return null;
        }
    }

    private Task<T> OnUiAsync<T>(Func<Task<T>> action)
    {
        var done = new TaskCompletionSource<T>();
        DispatcherQueue.TryEnqueue(async () =>
        {
            try { done.SetResult(await action()); }
            catch (Exception ex) { done.SetException(ex); }
        });
        return done.Task;
    }

    /// <summary>Asks for the code the speaker shows. Returns (code or null, wants the token instead).</summary>
    private async Task<(string? Code, bool WantsToken)> AskPairingCodeAsync(SendspinSpeaker speaker, bool dynamic, bool retry,
        bool offerToken, string? status)
    {
        var digits = dynamic ? 6 : 8;
        var box = new TextBox
        {
            PlaceholderText = dynamic ? "123-456" : "1234-5678",
            MaxLength = digits + 2,
            InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.Number) } },
        };
        var lines = new StackPanel { Spacing = 8 };
        if (retry)
            lines.Children.Add(new TextBlock { Text = "That code didn't match. Check it and try again.", TextWrapping = TextWrapping.Wrap });
        lines.Children.Add(new TextBlock
        {
            Text = dynamic
                ? $"{speaker.Name} shows a {digits}-digit code for LunaDrome (a Bluetooth bridge shows it on its web page). Type it here."
                : $"Type {speaker.Name}'s {digits}-digit pairing code (on its label or in its settings). It may ask for a button press first.",
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrEmpty(status) && !status.StartsWith("Waiting", StringComparison.Ordinal))
            lines.Children.Add(new TextBlock { Text = status, TextWrapping = TextWrapping.Wrap, Opacity = 0.7 });
        lines.Children.Add(box);
        var dialog = new ContentDialog
        {
            Title = $"Pair with {speaker.Name}",
            Content = lines,
            PrimaryButtonText = "Pair",
            SecondaryButtonText = offerToken ? "Use a pairing token" : "",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        ContentDialogResult result;
        try { result = await dialog.ShowAsync(); }
        catch { return (null, false); }
        return result switch
        {
            ContentDialogResult.Primary => (box.Text, false),
            ContentDialogResult.Secondary => (null, true),
            _ => (null, false),
        };
    }

    private async Task<string?> AskPairingTokenAsync(SendspinSpeaker speaker)
    {
        var box = new TextBox { PlaceholderText = "SP:0…", MinWidth = 320 };
        var dialog = new ContentDialog
        {
            Title = $"Pair with {speaker.Name}",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Paste the speaker's pairing token (starts with SP:0, from its label, leaflet or web page).", TextWrapping = TextWrapping.Wrap },
                    box,
                },
            },
            PrimaryButtonText = "Pair",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        try { return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text : null; }
        catch { return null; }
    }

    // ---- switching ----

    private async Task AddSendspinSpeakerAsync(SendspinSpeakerInfo info)
    {
        PlayOnThisDeviceFromDlna();   // one output at a time
        var output = EnsureSendspinOutput();
        _sendspinConnecting.Add(info.Key);
        _sendspinStatus = null;
        Log($"sendspin: connecting to {info.Name} ({info.Url})");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            SendspinSpeaker speaker;
            try
            {
                speaker = await output.AddSpeakerAsync(info.Url, timeout.Token);
            }
            catch (SendspinPairingRequiredException need)
            {
                _sendspinConnecting.Remove(info.Key);
                UpdateOutputButton();
                if (await PairSendspinAsync(need.Speaker) is not { } paired)
                    return;
                speaker = paired;
            }
            Log($"sendspin: connected to {speaker.Name} ({speaker.SoftwareVersion}, {(speaker.Encrypted ? $"encrypted, psk {speaker.PskCategory}" : "unencrypted")}), roles {string.Join(",", speaker.ActiveRoles)}");
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
        ShowToast($"Playing on {ThisDeviceName.ToLowerInvariant()}");
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
            ShowToast($"{speaker.Name} disconnected ({why}) - playing on {ThisDeviceName.ToLowerInvariant()}");
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
        ApplyPlaybackVolume();   // muted while followed; back to the slider on this device
        RestartVisualizer();
        Log($"sendspin: output {(on ? "on" : "off")} (was {(wasPlaying ? "playing" : "paused")} at {position:mm\\:ss})");

        if (JamOwnsPlayback)
        {
            ShowToast(on ? "The speakers take over with the next jam song" : $"{ThisDeviceName} takes over with the next jam song");
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
            if (!SendspinTap.TryAttach(player, source, DispatcherQueue, Log))
                Log("sendspin: couldn't reach the VLC player to take its audio");
            // The level LunaDrome wants (MediaPlayer.Volume may already hold VLC's 0 by now).
            SendspinTap.SetGain(player, ReferenceEquals(player, _player) && _fadingOut == null ? PlaybackVolume : 0);
        }
        else if (SendspinTap.IsAttached(player))
        {
            var followed = SendspinTap.MutesPlayer(player);
            SendspinTap.Detach(player);
            if (!followed && !LocalAudioModules.Any(module => VlcAudioOutput.TrySet(player, module)))
                Log("sendspin: couldn't switch the player back to the sound card");
            else
                ApplyAudioDevice(player);
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
