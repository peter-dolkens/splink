using SpLink.Protocol.Transport;

namespace SpLink.Protocol;

public sealed record SpProSnapshot(
    DateTimeOffset TakenAt, SpProLiveReading? Live, DateTime? InverterClock, string? Error,
    SpProUnitInfo? Unit = null, double? ClockDriftSeconds = null, DateTimeOffset? ClockReadAt = null,
    HostClockStatus? HostClock = null, SpProTodayReading? Today = null, DateTimeOffset? TodayReadAt = null,
    IReadOnlyList<SpProBlockReading>? Blocks = null, DateTimeOffset? BlocksReadAt = null,
    IReadOnlyList<SpProLogUpdate>? Logs = null, DateTimeOffset? LogsCheckedAt = null,
    DateTimeOffset? LiveReadAt = null)
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
    private readonly HostClockProbe _hostClock = new();
    private SpProTodayReading? _today;
    private DateTimeOffset _todayReadAt = DateTimeOffset.MinValue;
    private IReadOnlyList<SpProBlockReading>? _blocks;
    private DateTimeOffset _blocksReadAt = DateTimeOffset.MinValue;
    private SpProFastReading? _fast;
    private SpProLiveReading? _live;
    private DateTimeOffset _liveReadAt = DateTimeOffset.MinValue;
    private SpProLogWatcher? _logWatcher;
    private IReadOnlyList<SpProLogUpdate>? _logs;
    private DateTimeOffset _logsCheckedAt = DateTimeOffset.MinValue;

    /// <summary>How long a reading may be reused, so a burst of requests does not re-read the inverter.</summary>
    public TimeSpan CacheFor { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How often to re-read the 85-word live block. Zero reads it whenever the snapshot cache
    /// has expired, which is the historical behaviour.
    /// <para>
    /// Setting this decouples the live block from the request rate, which matters once a fast
    /// path exists: the handful of registers a shedding decision needs can then be polled every
    /// few seconds while the rest of the live data, which moves far more slowly, is read on its
    /// own schedule rather than dragged along.
    /// </para>
    /// </summary>
    public TimeSpan LiveInterval { get; set; } = TimeSpan.Zero;

    /// <summary>Quiet period after which the serial link is closed and the port released.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How often to re-read the inverter's clock. It is a separate serial round trip and the value
    /// only matters for spotting drift, so reading it on every poll would roughly double the traffic
    /// on the link for no benefit. Between reads the last value is reused.
    /// </summary>
    public TimeSpan ClockInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How often to re-read the "Today" accumulators. They are energy totals rather than
    /// instantaneous readings, so a slow cadence loses nothing and keeps the extra round trip
    /// off the link.
    /// </summary>
    public TimeSpan TodayInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How often to sweep every display block. That is nine round trips, so it runs rarely:
    /// the blocks are mostly accumulators and configuration-shaped values that barely move.
    /// </summary>
    public TimeSpan BlockSweepInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How often to check the log write pointers. Each check is four five-word reads, so this can
    /// be frequent; records are only paged when a pointer has actually moved.
    /// </summary>
    public TimeSpan LogCheckInterval { get; set; } = TimeSpan.FromMinutes(5);

    public Action<string>? Log { get; set; }

    /// <summary>True while the serial link is held open.</summary>
    public bool Connected => _client is not null;

    /// <summary>
    /// How long a fast reading may be reused. Short, because the whole point is freshness, but
    /// non-zero so several consumers asking at once do not each cost a round trip.
    /// </summary>
    public TimeSpan FastCacheFor { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Reads only the shedding-relevant words. Shares the session gate with everything else, so
    /// a fast poll and a block sweep cannot interleave on the wire.
    /// </summary>
    public async Task<SpProFastReading?> ReadFastAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _lastUsed = DateTimeOffset.Now;
            if (_fast is { } cached && DateTimeOffset.Now - cached.TakenAt < FastCacheFor) return cached;
            var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            return _fast = await client.ReadFastAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log?.Invoke($"fast read failed: {ex.Message}");
            await CloseAsync().ConfigureAwait(false);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reads live data, connecting first if necessary. Never throws; failures land in the snapshot.</summary>
    public async Task<SpProSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _lastUsed = DateTimeOffset.Now;
            if (_cached is { } c && DateTimeOffset.Now - c.TakenAt < CacheFor) return c;

            // Cheap and cached; it never throws, so it is safe to have in hand for the error path too.
            var host = _hostClock.Read();

            try
            {
                var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                if (_live is null || LiveInterval <= TimeSpan.Zero
                    || DateTimeOffset.Now - _liveReadAt >= LiveInterval)
                {
                    _live = await client.ReadLiveAsync(cancellationToken).ConfigureAwait(false);
                    _liveReadAt = DateTimeOffset.Now;
                }
                var live = _live;
                var unit = await client.ReadUnitInfoAsync(cancellationToken).ConfigureAwait(false);

                if (_clock is null || DateTimeOffset.Now - _clockReadAt >= ClockInterval)
                {
                    _clock = (await client.ReadClockAsync(cancellationToken).ConfigureAwait(false)).Time;
                    _clockReadAt = DateTimeOffset.Now;
                    // Drift is only meaningful at the instant it was measured, so capture it here
                    // rather than letting a consumer compare a stale timestamp against "now".
                    // DateTime.Now is this host's clock, which is why the measurement is only
                    // reported while that clock is known to be disciplined.
                    _clockDriftSeconds = Drift(host, SpProScaling.Round((_clock.Value - DateTime.Now).TotalSeconds, 1));
                }

                if (_today is null || DateTimeOffset.Now - _todayReadAt >= TodayInterval)
                {
                    _today = await client.ReadTodayAsync(cancellationToken).ConfigureAwait(false);
                    _todayReadAt = DateTimeOffset.Now;
                }

                if (_blocks is null || DateTimeOffset.Now - _blocksReadAt >= BlockSweepInterval)
                {
                    _blocks = await client.ReadAllBlocksAsync(cancellationToken).ConfigureAwait(false);
                    _blocksReadAt = DateTimeOffset.Now;
                }

                if (_logs is null || DateTimeOffset.Now - _logsCheckedAt >= LogCheckInterval)
                {
                    _logWatcher ??= new SpProLogWatcher(client) { Log = Log };
                    var updates = await _logWatcher.PollAsync(cancellationToken).ConfigureAwait(false);
                    // Carry forward the last records seen for each log, so a consumer polling
                    // faster than the check interval still sees them rather than an empty list.
                    _logs = Merge(_logs, updates);
                    _logsCheckedAt = DateTimeOffset.Now;
                }

                // Re-check at probe cadence rather than clock-read cadence, so losing NTP takes the
                // figure away within the minute instead of up to ClockInterval later.
                return _cached = new SpProSnapshot(DateTimeOffset.Now, live, _clock, null, unit,
                                                   Drift(host, _clockDriftSeconds), _clockReadAt, host,
                                                   _today, _todayReadAt, _blocks, _blocksReadAt,
                                                   _logs, _logsCheckedAt, _liveReadAt);
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
                                                   ex.Message, _cached?.Unit, Drift(host, _cached?.ClockDriftSeconds),
                                                   _cached?.ClockReadAt, host, _cached?.Today, _cached?.TodayReadAt,
                                                   _cached?.Blocks, _cached?.BlocksReadAt,
                                                   _cached?.Logs, _cached?.LogsCheckedAt, _liveReadAt);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Keeps the most recent non-empty record list for each log. A poll that found nothing new
    /// reports zero records, which is correct for that poll but would otherwise erase what the
    /// previous one found before a consumer had seen it.
    /// </summary>
    private static IReadOnlyList<SpProLogUpdate> Merge(
        IReadOnlyList<SpProLogUpdate>? previous, IReadOnlyList<SpProLogUpdate> latest)
    {
        if (previous is null) return latest;
        var carried = previous.ToDictionary(u => u.Type);
        return [.. latest.Select(u => u.Count > 0 || !carried.TryGetValue(u.Type, out var old)
                                      ? u
                                      : u with { Records = old.Records, CapturedAt = old.CapturedAt })];
    }

    /// <summary>
    /// Withholds a drift figure whose reference we know to be unsound. A drift is the difference
    /// between the inverter's clock and this host's, so an undisciplined host clock makes the
    /// number meaningless — and a meaningless number that still looks like a measurement is worse
    /// than no number at all. An <em>unverifiable</em> host clock (anywhere we cannot interrogate
    /// the OS) is not the same as a bad one, so the figure stands.
    /// </summary>
    internal static double? Drift(HostClockStatus host, double? measured) =>
        host.Synchronized == false ? null : measured;

    /// <summary>
    /// Reads an arbitrary register block through the live session.
    /// <para>
    /// The SP PRO admits one session at a time, so without this the only way to look at a block the
    /// bridge does not decode was to stop the service and take the port with a separate process.
    /// Doing that repeatedly wedged the USB device hard enough to need a device-level reset, so
    /// exploration goes through the gate like everything else. Read-only by construction: there is
    /// no write counterpart.
    /// </para>
    /// </summary>
    public async Task<ushort[]> ReadWordsAsync(uint address, int wordCount, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _lastUsed = DateTimeOffset.Now;
            var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            return await client.ReadWordsAsync(address, wordCount, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log?.Invoke($"raw read of {address} failed: {ex.Message}");
            await CloseAsync().ConfigureAwait(false);
            throw;
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
