namespace SpLink.Protocol;

/// <summary>What one log held when it was last checked, and whatever records were new.</summary>
public sealed record SpProLogUpdate(
    SpProLogType Type, uint CurrentAddress, int EntryCount, DateTimeOffset CheckedAt,
    IReadOnlyList<DecodedLogRecord> Records, DateTimeOffset CapturedAt)
{
    /// <summary>
    /// How many records the most recent capture produced. Records are carried between checks so a
    /// consumer polling faster than the check interval still sees them, which means this is "how
    /// many arrived in the batch dated <see cref="CapturedAt"/>", not "how many are new right now".
    /// </summary>
    public int Count => Records.Count;
}

/// <summary>
/// Captures logged records as they appear, without re-reading the whole log.
/// <para>
/// A full download is expensive — the detailed log alone is around 115 round trips, roughly
/// twenty-five times a sweep of every live block — so it is not something to do on a timer. But
/// each log publishes its write pointer in a five-word header, which costs about the same as one
/// live reading. Watching those pointers and paging only what moved makes steady-state capture
/// cheaper than the block sweep: four small reads to check, and one page when a record appears.
/// </para>
/// <para>
/// This matters because the logs hold what polling structurally cannot: discrete events with the
/// inverter's own timestamps, per-window minima and maxima that point sampling steps over, and
/// the periods when the bridge itself was not running. The buffers are circular and the detailed
/// log is already full, so anything not captured before it wraps is gone.
/// </para>
/// </summary>
public sealed class SpProLogWatcher(SpProClient client)
{
    private readonly Dictionary<SpProLogType, uint> _seen = [];
    private readonly Dictionary<SpProLogType, uint[]> _sectors = [];
    private SpProLogFormatVersions? _versions;

    /// <summary>
    /// How many records to pull on the first sight of a log. Enough to cover a restart or a short
    /// outage without the cost of reading everything; the detailed log records every 15 minutes,
    /// so the default spans about sixteen hours.
    /// </summary>
    public int StartupBackfill { get; set; } = 64;

    /// <summary>A cap on one update, so a long gap cannot produce an unbounded read.</summary>
    public int MaxRecordsPerUpdate { get; set; } = 256;

    public Action<string>? Log { get; set; }

    /// <summary>
    /// Checks every log's pointer and returns only what is new. Cheap when nothing has happened.
    /// </summary>
    public async Task<IReadOnlyList<SpProLogUpdate>> PollAsync(CancellationToken cancellationToken = default)
    {
        var scale = await client.ReadScaleFactorsAsync(cancellationToken).ConfigureAwait(false);
        _versions ??= await client.Logs.ReadFormatVersionsAsync(cancellationToken).ConfigureAwait(false);

        var updates = new List<SpProLogUpdate>();
        foreach (var type in Enum.GetValues<SpProLogType>())
        {
            SpProLogInfo info;
            try
            {
                info = await client.Logs.ReadInfoAsync(type, cancellationToken).ConfigureAwait(false);
            }
            catch (SpProException ex)
            {
                Log?.Invoke($"{type}: header read failed: {ex.Message}");
                continue;
            }
            if (info.IsEmpty) continue;

            var first = !_seen.TryGetValue(type, out var previous);
            if (!first && previous == info.CurrentAddress)
            {
                // Pointer has not moved, so there is nothing to page.
                updates.Add(new SpProLogUpdate(type, info.CurrentAddress, info.EntryCount,
                                               DateTimeOffset.Now, [], DateTimeOffset.MinValue));
                continue;
            }

            var wanted = first ? Math.Min(StartupBackfill, info.EntryCount)
                               : RecordsBetween(info, previous, info.CurrentAddress);
            wanted = Math.Clamp(wanted, 0, Math.Min(MaxRecordsPerUpdate, info.EntryCount));

            var records = new List<DecodedLogRecord>();
            if (wanted > 0)
            {
                if (!_sectors.TryGetValue(type, out var sectors))
                    _sectors[type] = sectors = await client.Logs.ReadSectorTableAsync(info, cancellationToken).ConfigureAwait(false);

                var raw = await client.Logs.ReadAllAsync(info, sectors, wanted, cancellationToken).ConfigureAwait(false);
                foreach (var r in raw)
                {
                    // Event logs pad with blank records; the sampled logs do not.
                    if (SpProLogDecoder.IsBlankEvent(r) && type is not (SpProLogType.Detailed or SpProLogType.DailySummary))
                        continue;
                    try { records.Add(SpProLogDecoder.Decode(r, _versions, scale)); }
                    catch (SpProException) { }
                }
                Log?.Invoke($"{type}: {records.Count} new record(s)");
            }

            _seen[type] = info.CurrentAddress;
            updates.Add(new SpProLogUpdate(type, info.CurrentAddress, info.EntryCount,
                                           DateTimeOffset.Now, records, DateTimeOffset.Now));
        }
        return updates;
    }

    /// <summary>
    /// How many records lie between two write-pointer positions, in entries rather than words,
    /// allowing for the pointer having wrapped past the end of the buffer.
    /// </summary>
    private static int RecordsBetween(SpProLogInfo info, uint previous, uint current)
    {
        var size = (uint)info.EntrySizeWords;
        if (size == 0) return 0;
        // Pointers advance by whole entries; a smaller current address means it wrapped.
        var delta = current >= previous ? current - previous : 0;
        if (delta == 0 && current != previous) return info.EntryCount;   // wrapped: take a bounded slice
        return (int)(delta / size);
    }
}
