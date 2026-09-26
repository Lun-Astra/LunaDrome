namespace LunaDrome.Core.Services;

/// <summary>
/// Keeps a device in step with the one shared jam on JamiDrome: polls its state, and - while
/// this device is the jam's speaker - sends the heartbeat that keeps it so. If the heartbeat
/// says another device took over (or this one timed out), <see cref="SpeakerLost"/> fires and
/// the app should go quiet. UI-free (events arrive on background threads) so the desktop and
/// phone apps can share it.
/// </summary>
public sealed class JamSession : IDisposable
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FastPoll = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SlowPoll = TimeSpan.FromSeconds(3);

    private readonly object _gate = new();
    private CancellationTokenSource? _pollCts;
    private CancellationTokenSource? _heartbeatCts;
    private readonly SemaphoreSlim _pollNow = new(0, 1);
    private bool _watching;

    public JamiDromeClient Client { get; } = new();
    public string DeviceId { get; }
    public string DeviceName { get; }

    /// <summary>The latest state; null until the first successful poll.</summary>
    public JamState? State { get; private set; }
    public bool IsSpeaker { get; private set; }
    /// <summary>Server clock minus this device's clock, from the latest poll.</summary>
    public double ClockOffsetSeconds { get; private set; }
    /// <summary>The last poll error (null after a successful one).</summary>
    public string? LastError { get; private set; }

    public event Action? StateChanged;
    /// <summary>Another device became the speaker (or this one timed out); argument = the new speaker, if any.</summary>
    public event Action<JamSpeaker?>? SpeakerLost;
    /// <summary>The server rejected the host token - log in again.</summary>
    public event Action? AuthLost;

    public JamSession(string deviceId, string deviceName)
    {
        DeviceId = deviceId;
        DeviceName = deviceName;
        Client.SessionId = deviceId;
    }

    public double ServerNow => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 + ClockOffsetSeconds;

    /// <summary>Where the shared timeline is in the current track, right now.</summary>
    public double ElapsedSeconds => State?.ElapsedAt(ServerNow) ?? 0;

    /// <summary>Polls while someone is looking (the Jam page) or this device is the speaker.</summary>
    public void SetWatching(bool watching)
    {
        lock (_gate)
        {
            _watching = watching;
            UpdatePollLoop();
        }
    }

    /// <summary>Asks for a fresh state now instead of at the next tick.</summary>
    public void PollSoon()
    {
        if (_pollNow.CurrentCount == 0)
            try { _pollNow.Release(); } catch (SemaphoreFullException) { }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!Client.IsLoggedIn)
            return;
        try
        {
            var state = await Client.GetStateAsync(cancellationToken);
            ClockOffsetSeconds = state.ServerTime - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            State = state;
            LastError = null;
        }
        catch (JamiDromeException ex) when (ex.IsAuthError)
        {
            LastError = ex.Message;
            AuthLost?.Invoke();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = ex.Message;
        }
        StateChanged?.Invoke();
    }

    /// <summary>"Play here": makes this device the speaker (un-pausing a jam that paused because
    /// nobody was playing it) and starts the heartbeat.</summary>
    public async Task ClaimSpeakerAsync()
    {
        await Client.ClaimSpeakerAsync(DeviceId, DeviceName);
        lock (_gate)
        {
            IsSpeaker = true;
            _heartbeatCts?.Cancel();
            _heartbeatCts = new CancellationTokenSource();
            _ = HeartbeatLoopAsync(_heartbeatCts.Token);
            UpdatePollLoop();
        }
        await RefreshAsync();
    }

    /// <summary>Stops being the speaker; the server pauses the jam right away.</summary>
    public async Task ReleaseSpeakerAsync(TimeSpan? timeout = null)
    {
        StopSpeaker();
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
        try { await Client.ReleaseSpeakerAsync(DeviceId, cts.Token); }
        catch { /* the server times the speaker out anyway */ }
        PollSoon();
    }

    private void StopSpeaker()
    {
        lock (_gate)
        {
            IsSpeaker = false;
            _heartbeatCts?.Cancel();
            _heartbeatCts = null;
            UpdatePollLoop();
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                try
                {
                    var (active, speaker) = await Client.HeartbeatAsync(DeviceId, token);
                    if (!active)
                    {
                        StopSpeaker();
                        SpeakerLost?.Invoke(speaker);
                        return;
                    }
                }
                catch (JamiDromeException ex) when (ex.IsAuthError)
                {
                    StopSpeaker();
                    AuthLost?.Invoke();
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A dropped heartbeat or two is fine - the server allows ~20s.
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // Must hold _gate.
    private void UpdatePollLoop()
    {
        var wanted = (_watching || IsSpeaker) && Client.IsLoggedIn;
        if (wanted && _pollCts == null)
        {
            _pollCts = new CancellationTokenSource();
            _ = PollLoopAsync(_pollCts.Token);
        }
        else if (!wanted && _pollCts != null)
        {
            _pollCts.Cancel();
            _pollCts = null;
        }
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await RefreshAsync(token);
                // The speaker needs track changes within a second; the page alone can be lazier.
                var delay = IsSpeaker ? FastPoll : SlowPoll;
                await _pollNow.WaitAsync(delay, token);
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _pollCts?.Cancel();
            _heartbeatCts?.Cancel();
            _pollCts = null;
            _heartbeatCts = null;
        }
    }
}
