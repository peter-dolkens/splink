namespace SpLink.Fronius;

/// <summary>The outcome of querying one Fronius inverter.</summary>
public sealed record FroniusResult(
    string Name, string Host, DateTimeOffset TakenAt, FroniusReading? Reading, string? Error,
    string? CustomName = null, double? RatedWatts = null)
{
    public bool Healthy => Error is null && Reading is not null;
}

/// <summary>
/// Queries Fronius inverters on demand.
/// <para>
/// Unlike the SP PRO — whose serial link admits a single session, forcing a background poller — the Solar
/// API is stateless HTTP, so readings are fetched per request and are always current. A short cache only
/// coalesces bursts (a metrics scrape and a dashboard arriving together) rather than introducing staleness.
/// </para>
/// <para>
/// Inverters are queried concurrently and isolated: one that is powered down, unplugged or simply slow
/// yields an error on its own entry within the timeout, and never throws or affects the others.
/// </para>
/// </summary>
public sealed class FroniusService : IAsyncDisposable
{
    private readonly FroniusClient _client;
    private readonly HttpClient _http;
    private readonly Dictionary<string, (string? Name, double? Rated)> _identity = new();
    private readonly Lock _gate = new();
    private IReadOnlyList<FroniusResult>? _cached;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;
    private Task<IReadOnlyList<FroniusResult>>? _inFlight;

    public FroniusService(IReadOnlyList<FroniusInverterConfig> inverters, TimeSpan? requestTimeout = null)
    {
        Inverters = inverters;
        // Fail fast: an unreachable inverter must not hold up the caller's request.
        _http = new HttpClient { Timeout = requestTimeout ?? TimeSpan.FromSeconds(4) };
        _client = new FroniusClient(_http);
    }

    public IReadOnlyList<FroniusInverterConfig> Inverters { get; }

    /// <summary>How long a reading may be reused to coalesce concurrent callers. Zero disables caching.</summary>
    public TimeSpan CacheFor { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Returns the current readings, fetching only if the cache has expired.
    /// <para>
    /// Single-flight: callers arriving while a fetch is in progress join that fetch rather than
    /// starting another. Without this, a burst of concurrent requests would each miss the cache
    /// and stampede the inverters — and since the bridge is published on a public hostname, that
    /// burst is not under our control. Together with the cache this bounds inverter traffic to at
    /// most one round of requests per <see cref="CacheFor"/> window however often we are called.
    /// </para>
    /// </summary>
    public Task<IReadOnlyList<FroniusResult>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_cached is not null && DateTimeOffset.Now - _cachedAt < CacheFor)
                return Task.FromResult(_cached);
            return _inFlight ??= FetchAsync();
        }
    }

    private async Task<IReadOnlyList<FroniusResult>> FetchAsync()
    {
        try
        {
            // Deliberately not tied to any caller's token: one participant giving up must not
            // cancel the shared fetch for everyone else. HttpClient.Timeout still bounds it.
            var results = await Task.WhenAll(
                Inverters.Select(i => ReadOneAsync(i, CancellationToken.None))).ConfigureAwait(false);
            lock (_gate)
            {
                _cached = results;
                _cachedAt = DateTimeOffset.Now;
            }
            return results;
        }
        finally
        {
            lock (_gate) _inFlight = null;
        }
    }

    /// <summary>Never throws: a failure is returned as the result's <see cref="FroniusResult.Error"/>.</summary>
    private async Task<FroniusResult> ReadOneAsync(FroniusInverterConfig inverter, CancellationToken cancellationToken)
    {
        try
        {
            var reading = await _client.ReadAsync(inverter.Host, cancellationToken).ConfigureAwait(false);

            // Identity is static, so look it up once per inverter and only after it has answered.
            (string? Name, double? Rated) identity;
            lock (_gate) _identity.TryGetValue(inverter.Name, out identity);
            if (identity.Name is null)
            {
                identity = await _client.ReadInfoAsync(inverter.Host, cancellationToken).ConfigureAwait(false);
                lock (_gate) _identity[inverter.Name] = identity;
            }

            return new FroniusResult(inverter.Name, inverter.Host, DateTimeOffset.Now, reading, null, identity.Name, identity.Rated);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new FroniusResult(inverter.Name, inverter.Host, DateTimeOffset.Now, null, "request cancelled");
        }
        catch (Exception ex)
        {
            return new FroniusResult(inverter.Name, inverter.Host, DateTimeOffset.Now, null, ex.Message);
        }
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
