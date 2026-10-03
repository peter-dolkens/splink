namespace SpLink.Protocol;

public enum SpProLogType
{
    /// <summary>Alert events — faults and warnings.</summary>
    AlertEvents,
    /// <summary>Operational events — normal state changes.</summary>
    OperationalEvents,
    /// <summary>One record per day of totals.</summary>
    DailySummary,
    /// <summary>Periodic averages of every measured parameter (the high-resolution log).</summary>
    Detailed,
}

/// <summary>Register addresses for one log. Every log exposes the same shape, at a different base.</summary>
public sealed record SpProLogRegisters(
    SpProLogType Type, uint NumberOfSectors, uint EntrySize, uint CurrentAddressLo,
    uint EntryCount, uint SectorTable, uint RequiredStartDate, uint SearchStatus)
{
    public static readonly IReadOnlyDictionary<SpProLogType, SpProLogRegisters> All =
        new Dictionary<SpProLogType, SpProLogRegisters>
        {
            [SpProLogType.AlertEvents] = new(SpProLogType.AlertEvents, 41574, 41575, 41576, 41578, 41579, 41619, 41623),
            [SpProLogType.OperationalEvents] = new(SpProLogType.OperationalEvents, 41639, 41640, 41641, 41643, 41644, 41684, 41688),
            [SpProLogType.DailySummary] = new(SpProLogType.DailySummary, 41704, 41705, 41706, 41708, 41709, 41761, 41765),
            [SpProLogType.Detailed] = new(SpProLogType.Detailed, 41781, 41782, 41783, 41785, 41786, 41844, 41848),
        };
}

/// <summary>The five-word header describing one log's current state.</summary>
public sealed record SpProLogInfo(SpProLogType Type, int SectorCount, int EntrySizeWords, uint CurrentAddress, int EntryCount)
{
    /// <summary>Entries that fit in one 256-word read.</summary>
    public int EntriesPerRead => EntrySizeWords > 0 ? 256 / EntrySizeWords : 0;
    public bool IsEmpty => EntryCount == 0 || EntrySizeWords == 0;
}

/// <summary>Log-record format versions, which select the record layout variant.</summary>
public sealed record SpProLogFormatVersions(int Event, int Detailed, int DailySummary)
{
    public int For(SpProLogType type) => type switch
    {
        SpProLogType.AlertEvents or SpProLogType.OperationalEvents => Event,
        SpProLogType.Detailed => Detailed,
        _ => DailySummary,
    };
}

/// <summary>One log record as raw words, with the address it came from.</summary>
public sealed record SpProLogRecord(SpProLogType Type, uint Address, ushort[] Words);

/// <summary>
/// Downloads the SP PRO's logged performance data.
/// <para>
/// Strictly read-only. SP LINK can ask the inverter to search a date range, but that works by *writing*
/// the range and a trigger flag into the device, so it is deliberately not used here: we page the whole
/// log and let the caller filter by timestamp instead.
/// </para>
/// </summary>
public sealed class SpProLogReader(SpProClient client)
{
    /// <summary>Raised with (recordsSoFar, totalExpected) as paging proceeds.</summary>
    public Action<int, int>? Progress { get; set; }

    public async Task<SpProLogFormatVersions> ReadFormatVersionsAsync(CancellationToken cancellationToken = default)
    {
        var words = await client.ReadWordsAsync(40967, 25, cancellationToken).ConfigureAwait(false);
        return new SpProLogFormatVersions(words[3], words[4], words[5]);
    }

    public async Task<SpProLogInfo> ReadInfoAsync(SpProLogType type, CancellationToken cancellationToken = default)
    {
        var r = SpProLogRegisters.All[type];
        var w = await client.ReadWordsAsync(r.NumberOfSectors, 5, cancellationToken).ConfigureAwait(false);
        return new SpProLogInfo(type, w[0], w[1], (uint)(w[2] | (w[3] << 16)), w[4]);
    }

    /// <summary>
    /// Reads the sector table: a flat array where index 2k is sector k's first word address and
    /// 2k+1 is its last word address, inclusive.
    /// </summary>
    public async Task<uint[]> ReadSectorTableAsync(SpProLogInfo info, CancellationToken cancellationToken = default)
    {
        var r = SpProLogRegisters.All[info.Type];
        int words = info.SectorCount * 4;
        if (words is < 1 or > 256)
            throw new SpProProtocolException($"{info.Type}: implausible sector count {info.SectorCount}");

        var raw = await client.ReadWordsAsync(r.SectorTable, words, cancellationToken).ConfigureAwait(false);
        var sectors = new uint[raw.Length / 2];
        for (int i = 0; i < sectors.Length; i++)
            sectors[i] = (uint)(raw[2 * i] | (raw[2 * i + 1] << 16));
        return sectors;
    }

    /// <summary>
    /// Pages every record out of the log, newest first. Mirrors SP LINK's walk: start at the current
    /// entry and step backwards, never crossing a sector boundary, wrapping to the previous sector
    /// (and from sector 0 to the last) because the sectors form one circular buffer.
    /// </summary>
    public async Task<IReadOnlyList<SpProLogRecord>> ReadAllAsync(SpProLogInfo info, uint[] sectors, int? limit = null, CancellationToken cancellationToken = default)
    {
        var records = new List<SpProLogRecord>();
        if (info.IsEmpty) return records;

        int size = info.EntrySizeWords;
        int perRead = info.EntriesPerRead;
        if (perRead < 1) throw new SpProProtocolException($"{info.Type}: entry size {size} exceeds one read");

        int sector = FindSector(sectors, info.CurrentAddress);
        if (sector < 0)
            throw new SpProProtocolException($"{info.Type}: current log address 0x{info.CurrentAddress:X} is in no sector");

        int wanted = Math.Min(limit ?? info.EntryCount, Capacity(sectors, size));
        uint address = info.CurrentAddress;
        int got = 0;
        int batch = 1; // SP LINK always reads the newest entry on its own first.

        while (got < wanted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var words = await client.ReadWordsAsync(address, batch * size, cancellationToken).ConfigureAwait(false);

            // Within one response records run in ascending address order, so emit them newest-first.
            for (int i = batch - 1; i >= 0; i--)
                records.Add(new SpProLogRecord(info.Type, address + (uint)(i * size), words[(i * size)..((i + 1) * size)]));

            got += batch;
            Progress?.Invoke(got, wanted);
            if (got >= wanted) break;

            // How many whole entries fit below this read without leaving the sector or the budget?
            int fit = 1;
            while (address - (uint)(fit * size) >= sectors[sector * 2] && got + fit <= wanted && fit != perRead + 1)
                fit++;

            if (fit == 1)
            {
                sector = sector != 0 ? sector - 1 : sectors.Length / 2 - 1;
                int inSector = (int)((sectors[sector * 2 + 1] - sectors[sector * 2] + 1) / (uint)size);
                if (inSector < 1) throw new SpProProtocolException($"{info.Type}: sector {sector} holds no whole entries");
                address = sectors[sector * 2] + (uint)((inSector - 1) * size);
                batch = 1;
            }
            else
            {
                address -= (uint)((fit - 1) * size);
                batch = fit - 1;
            }
        }
        return records;
    }

    private static int FindSector(uint[] sectors, uint address)
    {
        for (int s = 0; s < sectors.Length / 2; s++)
            if (address >= sectors[s * 2] && address <= sectors[s * 2 + 1]) return s;
        return -1;
    }

    private static int Capacity(uint[] sectors, int size)
    {
        int total = 0;
        for (int s = 0; s < sectors.Length / 2; s++)
            total += (int)((sectors[s * 2 + 1] - sectors[s * 2] + 1) / (uint)size);
        return total;
    }
}
