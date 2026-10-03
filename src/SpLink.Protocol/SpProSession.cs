using SpLink.Protocol.Transport;

namespace SpLink.Protocol;

public sealed record SpProSnapshot(
    DateTimeOffset TakenAt, SpProLiveReading? Live, DateTime? InverterClock, string? Error,
    SpProUnitInfo? Unit = null, double? ClockDriftSeconds = null, DateTimeOffset? ClockReadAt = null)
{
    public bool Healthy => Error is null && Live is not null;
}

/// <summary>
/// An on-demand SP PRO session.
/// <para>
/// Nothing is read until something asks, so an idle bridge puts no traffic on the inverter. The serial
/// link is expensive to establish (auto-baud plus the login handshake), so once connected the session is
/// kept warm while requests keep arriving and is dropped after <see cref="IdleTimeout"/> of quiet. That
/// also releases the port — the SP PRO admits only one session at a time, so an idle bridge no longer
/// blocks SP LINK or any other tool from connecting.
/// </para>
/// </summary>
public sealed class SpProSession(Func<Task<ISpProTransport>> openTransport, string password = SpProLogin.DefaultPassword) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SpProClient? _client;
    private SpProSnapshot? _cached;
    private DateTimeOffset _lastUsed = DateTimeOffset.MinValue;
    private DateTime? _clock;
    private DateTimeOffset _clockReadAt = DateTimeOffset.MinValue;
    private double? _clockDriftSeconds;

    /// <summary>How long a reading may be reused, so a burst of requests does not re-read the inverter.</summary>
    public TimeSpan CacheFor { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Quiet period after which the serial link is closed and the port released.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How often to re-read the inverter's clock. It is a separate serial round trip and the value
    /// only matters for spotting drift, so reading it on every poll would roughly double the traffic
    /// on the link for no benefit. Between reads the last value is reused.
    /// </summary>
    public TimeSpan ClockInterval { get; set; } = TimeSpan.FromMinutes(10);

    public Action<string>? Log { get; set; }

    /// <summary>True while the serial link is held open.</summary>
    public bool Connected => _client is not null;

    /// <summary>Reads live data, connecting first if necessary. Never throws; failures land in the snapshot.</summary>
    public async Task<SpProSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _lastUsed = DateTimeOffset.Now;
            if (_cached is { } c && DateTimeOffset.Now - c.TakenAt < CacheFor) return c;

            try
            {
                var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                var live = await client.ReadLiveAsync(cancellationToken).ConfigureAwait(false);
                var unit = await client.ReadUnitInfoAsync(cancellationToken).ConfigureAwait(false);

                if (_clock is null || DateTimeOffset.Now - _clockReadAt >= ClockInterval)
                {
                    _clock = (await client.ReadClockAsync(cancellationToken).ConfigureAwait(false)).Time;
                    _clockReadAt = DateTimeOffset.Now;
                    // Drift is only meaningful at the instant it was measured, so capture it here
                    // rather than letting a consumer compare a stale timestamp against "now".
                    _clockDriftSeconds = Math.Round((_clock.Value - DateTime.Now).TotalSeconds, 1);
                }

                return _cached = new SpProSnapshot(DateTimeOffset.Now, live, _clock, null, unit,
                                                   _clockDriftSeconds, _clockReadAt);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Drop the link so the next request reconnects cleanly rather than reusing a broken one.
                Log?.Invoke($"sp pro read failed: {ex.Message}");
                await CloseAsync().ConfigureAwait(false);
                return _cached = new SpProSnapshot(DateTimeOffset.Now, _cached?.Live, _cached?.InverterClock,
                                                   ex.Message, _cached?.Unit, _cached?.ClockDriftSeconds, _cached?.ClockReadAt);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Closes the link once it has been idle, freeing the port. Run for the lifetime of the host.</summary>
    public async Task RunIdleReaperAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            if (!Connected || DateTimeOffset.Now - _lastUsed < IdleTimeout) continue;

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Connected && DateTimeOffset.Now - _lastUsed >= IdleTimeout)
                {
                    Log?.Invoke($"idle for {IdleTimeout.TotalSeconds:0}s — releasing the SP PRO port");
                    await CloseAsync().ConfigureAwait(false);
                }
            }
            finally { _gate.Release(); }
        }
        await CloseAsync().ConfigureAwait(false);
    }

    private async Task<SpProClient> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_client is not null) return _client;
        Log?.Invoke("connecting to the SP PRO ...");
        var transport = await openTransport().ConfigureAwait(false);
        var client = new SpProClient(transport); // read-only
        try
        {
            var info = await client.ConnectAsync(password, cancellationToken).ConfigureAwait(false);
            Log?.Invoke($"connected: link port {info.LinkPort}, {(info.LoggedIn ? "login accepted" : "no login required")}");
            return _client = client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task CloseAsync()
    {
        if (_client is null) return;
        var client = _client;
        _client = null;
        try { await client.DisconnectAsync().ConfigureAwait(false); } catch (Exception) { }
        try { await client.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
