using LunaDrome.Controls;
using LunaDrome.Core.Dlna;
using LunaDrome.Services;

namespace LunaDrome;

// Output picker, DLNA part: play on a DLNA / UPnP renderer (TV, AV receiver, network speaker).
// Unlike Sendspin the renderer fetches the song from Navidrome itself, so there's one renderer
// at a time and no multi-room sync; DlnaFollower keeps it on the player's song and position
// while the player runs muted here.
public sealed partial class MainPage
{
    private IReadOnlyList<DlnaRendererInfo> _dlnaFound = [];
    private Task? _dlnaScan;
    private string? _dlnaConnecting;
    private DlnaFollower? _dlna;

    private bool DlnaActive => _dlna != null;

    private async Task ScanDlnaAsync()
    {
        if (_dlnaScan is { IsCompleted: false })
        {
            await _dlnaScan;
            return;
        }
        _dlnaScan = Task.Run(async () =>
        {
            try
            {
#if ANDROID
                var wifi = (Android.Net.Wifi.WifiManager?)Android.App.Application.Context.GetSystemService(Android.Content.Context.WifiService);
                var multicast = wifi?.CreateMulticastLock("LunaDrome DLNA discovery");
                multicast?.Acquire();
                IReadOnlyList<DlnaRendererInfo> found;
                try { found = await DlnaDiscovery.SearchAsync(TimeSpan.FromSeconds(3), CancellationToken.None); }
                finally { multicast?.Release(); }
#else
                var found = await DlnaDiscovery.SearchAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
#endif
                Log($"dlna: found {found.Count} renderer(s): {string.Join(", ", found.Select(f => $"{f.Name} ({f.Manufacturer} {f.Model}) {f.Description}"))}");
                _dlnaFound = found;
            }
            catch (Exception ex)
            {
                Log($"dlna: discovery failed: {ex.Message}");
            }
        });
        await _dlnaScan;
    }

    /// <summary>The renderers in the "Play on" menu, below the Sendspin speakers.</summary>
    private void AddDlnaItems(MenuFlyout flyout)
    {
        var entries = _dlnaFound.ToList();
        if (_dlna != null && entries.All(e => e.Key != _dlna.Renderer.Info.Key))
            entries.Insert(0, _dlna.Renderer.Info);   // stays listed even when a scan missed it
        if (entries.Count == 0)
            return;
        flyout.Items.Add(new MenuFlyoutSeparator());
        foreach (var info in entries)
        {
            var active = _dlna?.Renderer.Info.Key == info.Key;
            var connecting = _dlnaConnecting == info.Key;
            var item = new ToggleMenuFlyoutItem
            {
                Text = connecting ? $"{info.Name} (connecting…)" : info.Name,
                IsChecked = active,
                IsEnabled = !connecting,
            };
            RichMenu.SetIcon(item, MenuIcons.Tv);
            var model = string.Join(" ", new[] { info.Manufacturer, info.Model }.Where(s => !string.IsNullOrWhiteSpace(s)));
            ToolTipService.SetToolTip(item, $"DLNA renderer{(model.Length > 0 ? $" · {model}" : "")} · {info.Description.Host}");
            RichMenu.OnClick(item, () =>
            {
                if (active) PlayOnThisDeviceFromDlna();
                else _ = StartDlnaAsync(info);
            });
            flyout.Items.Add(item);
        }
    }

    private async Task StartDlnaAsync(DlnaRendererInfo info)
    {
        _dlnaConnecting = info.Key;
        UpdateOutputButton();
        try
        {
            // One output at a time: Sendspin speakers or another renderer hand over first.
            if (_sendspinActive)
                await PlayOnThisComputerAsync();
            StopDlna();
            var renderer = new DlnaRenderer(info);
            // Reachable and willing? (also wakes some renderers up before the song arrives)
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
                await renderer.GetStatusAsync(timeout.Token);
            if (Uri.TryCreate(_client.ServerUrl, UriKind.Absolute, out var server) && server.IsLoopback)
                Log($"dlna: the server address {server.Host} is this device only - {info.Name} can't fetch songs from it");

            FinishCrossfade();
            _dlna = DlnaFollower.Attach(_player, renderer, DescribeForDlna, DispatcherQueue, Log);
            _dlna.Failed += reason => DispatcherQueue.TryEnqueue(() =>
            {
                if (_dlna?.Renderer != renderer) return;
                StopDlna();
                ShowToast($"{info.Name} stopped answering ({reason}) - playing on {ThisDeviceName.ToLowerInvariant()}");
                UpdateOutputButton();
            });
            _dlna.RemoteCommand += command => DispatcherQueue.TryEnqueue(() => HandleMediaCommand(command));
            ApplyPlaybackVolume();   // muted here; the slider moves the renderer's volume from now on
            RestartVisualizer();
            Log($"dlna: output on {info.Name} ({info.AvTransportControl})");
            ShowToast($"Playing on {info.Name}");
        }
        catch (Exception ex) when (ex is DlnaException or HttpRequestException or TaskCanceledException or System.Xml.XmlException)
        {
            Log($"dlna: connecting to {info.Name} failed: {ex}");
            ShowToast($"Couldn't connect to {info.Name}: {(ex is TaskCanceledException ? "no answer" : ex.Message)}");
        }
        finally
        {
            _dlnaConnecting = null;
            UpdateOutputButton();
        }
    }

    /// <summary>Stops the renderer; the player (already at the same spot) is heard here again.</summary>
    private void StopDlna()
    {
        if (_dlna == null)
            return;
        Log($"dlna: output off ({_dlna.Renderer.Name})");
        _dlna = null;
        DlnaFollower.Detach(_player);
        ApplyPlaybackVolume();
        RestartVisualizer();
    }

    private void PlayOnThisDeviceFromDlna()
    {
        StopDlna();
        UpdateOutputButton();
    }

    /// <summary>What the renderer gets for the player's song (from <paramref name="from"/> on):
    /// its own stream address from Navidrome (never the prefetched file on this device), in a
    /// format TVs and receivers play.</summary>
    private DlnaTrack? DescribeForDlna(Uri playerUri, TimeSpan from)
    {
        if (!JamOwnsPlayback && _loadedEntry?.Song is { } song)
        {
            var (url, mime, converted) = _client.GetDeviceStreamUrl(song.Id, from);
            return new DlnaTrack(new Uri(url), song.Title, song.ArtistName, song.AlbumName,
                Uri.TryCreate(song.CoverArtUrl, UriKind.Absolute, out var art) ? art : null,
                mime, TimeSpan.FromSeconds(song.DurationSeconds), converted ? TimeSpan.FromSeconds((int)from.TotalSeconds) : TimeSpan.Zero, converted);
        }
        // A jam song: the renderer fetches the same address the player does.
        return playerUri.Scheme is "http" or "https"
            ? new DlnaTrack(playerUri, NowPlayingText.Text is { Length: > 0 } title ? title : "LunaDrome", null, null, null, "*", TimeSpan.Zero)
            : null;
    }
}
