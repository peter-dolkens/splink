using System.Buffers.Binary;
using SpLink.Protocol.Transport;

namespace SpLink.Protocol;

public sealed record SpProConnectionInfo(int LinkPort, int BaudCode, string BaudDescription, bool LoggedIn);

public sealed record SpProClockReading(DateTime Time, DayOfWeek StoredDayOfWeek, ushort[] RawWords)
{
    /// <summary>True when the separately stored weekday agrees with the calendar date.</summary>
    public bool DayOfWeekConsistent => StoredDayOfWeek == Time.DayOfWeek;
}

/// <summary>
/// High-level SP PRO session: connect/login as SP LINK does, then read live data and the real-time clock.
/// <para>
/// Read-only by default. Because this talks to live power equipment, every write is refused unless the caller
/// opts in with <c>allowWrites</c>. The only exceptions are the connection-lifecycle registers (the login
/// challenge response and the disconnect notification), which are volatile protocol state, not device settings.
/// </para>
/// </summary>
public sealed class SpProClient(ISpProTransport transport, bool allowWrites = false) : IAsyncDisposable
{
    private int _linkPort = -1;
    private SpProScaleFactors? _scaleFactors;
    private int? _memoryMapVersion;
    private SpProUnitInfo? _unitInfo;
    private SpProLogReader? _logs;

    public ISpProTransport Transport { get; } = transport;
    public FrameChannel Channel { get; } = new(transport);

    /// <summary>When false (the default) any write outside the connection handshake throws <see cref="SpProReadOnlyException"/>.</summary>
    public bool AllowWrites { get; } = allowWrites;

    /// <summary>Set false to skip the courtesy "SP LINK disconnecting" write on <see cref="DisconnectAsync"/>.</summary>
    public bool NotifyOnDisconnect { get; set; } = true;
    public SpProConnectionInfo? Connection { get; private set; }

    public Action<string>? Trace
    {
        get => Channel.Trace;
        set => Channel.Trace = value;
    }

    /// <summary>
    /// Reproduces SP LINK's connection sequence: detect the unit via the link-port register, run the MD5
    /// challenge/response login if the unit asks for it, then confirm the port and its configured speed.
    /// </summary>
    public async Task<SpProConnectionInfo> ConnectAsync(string password = SpProLogin.DefaultPassword, CancellationToken cancellationToken = default)
    {
        var portResponse = await Channel.ExchangeAsync(SpProFrame.BuildRead(SpProRegisters.LinkPort, 1), cancellationToken).ConfigureAwait(false);
        if (portResponse[2] == 0x5A && portResponse[3] == 0x5A && portResponse[4] == 0x5A && portResponse[5] == 0x5A)
            throw new SpProMultiPhaseBlockedException("Connection refused: in a Powerchain system connect to the L1 System Manager SP PRO");

        int port = BinaryPrimitives.ReadUInt16LittleEndian(portResponse.AsSpan(SpProFrame.HeaderLength, 2));
        bool loggedIn = false;

        if (port == 0xFFFF)
        {
            var challengeResponse = await Channel.ExchangeAsync(SpProFrame.BuildRead(SpProRegisters.LoginChallenge, 8), cancellationToken).ConfigureAwait(false);
            var challenge = challengeResponse.AsSpan(SpProFrame.HeaderLength, SpProLogin.ChallengeLength);
            var answer = SpProLogin.ComputeResponseWords(challenge, password);

            await Channel.ExchangeAsync(SpProFrame.BuildWrite(SpProRegisters.LoginChallenge, answer), cancellationToken).ConfigureAwait(false);

            var result = await Channel.ExchangeAsync(SpProFrame.BuildRead(SpProRegisters.LoginResult, 1), cancellationToken).ConfigureAwait(false);
            if (result[SpProFrame.HeaderLength] != 1)
                throw new SpProLoginException("SP PRO login failed: incorrect password");
            loggedIn = true;

            portResponse = await Channel.ExchangeAsync(SpProFrame.BuildRead(SpProRegisters.LinkPort, 1), cancellationToken).ConfigureAwait(false);
            port = BinaryPrimitives.ReadUInt16LittleEndian(portResponse.AsSpan(SpProFrame.HeaderLength, 2));
        }

        uint baudRegister = port switch
        {
            1 => SpProRegisters.Port1BaudCode,
            2 => SpProRegisters.Port2BaudCode,
            _ => throw new SpProProtocolException($"Unexpected SP PRO link port {port} (expected 1 or 2)"),
        };
        var baudResponse = await Channel.ExchangeAsync(SpProFrame.BuildRead(baudRegister, 1), cancellationToken).ConfigureAwait(false);
        int baudCode = baudResponse[SpProFrame.HeaderLength];
        var baudDescription = SpProRegisters.BaudCodeToString(baudCode);
        if (baudDescription is "Auto" or "Error")
            throw new SpProProtocolException($"SP PRO reported an unusable port speed code {baudCode}");

        _linkPort = port;
        Connection = new SpProConnectionInfo(port, baudCode, baudDescription, loggedIn);
        return Connection;
    }

    public async Task<ushort[]> ReadWordsAsync(uint address, int wordCount, CancellationToken cancellationToken = default)
    {
        var response = await Channel.ExchangeAsync(SpProFrame.BuildRead(address, wordCount), cancellationToken).ConfigureAwait(false);
        return SpProFrame.DataWords(response);
    }

    public async Task WriteWordsAsync(uint address, ReadOnlyMemory<ushort> words, CancellationToken cancellationToken = default)
    {
        if (!AllowWrites && !SpProRegisters.IsConnectionLifecycleRegister(address))
            throw new SpProReadOnlyException(
                $"Refusing to write to register 0x{address:X} ({address}): this client is read-only. "
                + "Construct SpProClient with allowWrites: true to permit writes to the inverter.");

        var request = SpProFrame.BuildWrite(address, words.Span);
        var response = await Channel.ExchangeAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.AsSpan().SequenceEqual(request))
            throw new SpProProtocolException($"SP PRO did not echo the write to 0x{address:X}");
    }

    /// <summary>Reads the model-specific analogue scaling constants, caching them for the session.</summary>
    public async Task<SpProScaleFactors> ReadScaleFactorsAsync(CancellationToken cancellationToken = default)
    {
        if (_scaleFactors is not null) return _scaleFactors;
        var words = await ReadWordsAsync(SpProRegisters.CommonScaleFactors, 6, cancellationToken).ConfigureAwait(false);
        return _scaleFactors = SpProScaleFactors.FromWords(words);
    }

    /// <summary>Reads and decodes the live "Now" data: SoC, battery, AC load, charger and generator state.</summary>
    public async Task<SpProLiveReading> ReadLiveAsync(CancellationToken cancellationToken = default)
    {
        var scale = await ReadScaleFactorsAsync(cancellationToken).ConfigureAwait(false);
        var words = await ReadWordsAsync(SpProRegisters.NowBlock, SpProRegisters.NowBlockWordCount, cancellationToken).ConfigureAwait(false);
        return SpProLiveData.Decode(words, scale);
    }

    /// <summary>
    /// Reads the memory-map version, cached for the session. Several blocks moved fields between
    /// versions, so decoders are told the version rather than assuming the newest layout.
    /// </summary>
    public async Task<int> ReadMemoryMapVersionAsync(CancellationToken cancellationToken = default)
    {
        if (_memoryMapVersion is { } cached) return cached;
        var words = await ReadWordsAsync(SpProRegisters.ConfigStatus, SpProRegisters.ConfigStatusWordCount,
                                         cancellationToken).ConfigureAwait(false);
        return (_memoryMapVersion = words[SpProRegisters.MemoryMapVersionOffset]).Value;
    }

    /// <summary>Reads and decodes the "Today" accumulators: energy in and out, run hours, AC-coupled totals.</summary>
    public async Task<SpProTodayReading> ReadTodayAsync(CancellationToken cancellationToken = default)
    {
        var scale = await ReadScaleFactorsAsync(cancellationToken).ConfigureAwait(false);
        var version = await ReadMemoryMapVersionAsync(cancellationToken).ConfigureAwait(false);
        var words = await ReadWordsAsync(SpProRegisters.TodayBlock, SpProRegisters.TodayBlockWordCount,
                                         cancellationToken).ConfigureAwait(false);
        return SpProTodayData.Decode(words, scale, version);
    }

    /// <summary>Access to the logged performance data (read-only).</summary>
    public SpProLogReader Logs => _logs ??= new SpProLogReader(this);

    /// <summary>Reads the inverter's model, serial number and hardware revision.</summary>
    public async Task<SpProUnitInfo> ReadUnitInfoAsync(CancellationToken cancellationToken = default)
    {
        if (_unitInfo is not null) return _unitInfo;
        var words = await ReadWordsAsync(SpProRegisters.UnitInfo, 4, cancellationToken).ConfigureAwait(false);
        return _unitInfo = SpProUnitInfo.FromWords(words);
    }

    /// <summary>Reads the inverter's full configuration. Read-only; there is no configuration-write path.</summary>
    public Task<SpProConfiguration> ReadConfigurationAsync(CancellationToken cancellationToken = default)
        => SpProConfigReader.ReadAsync(this, cancellationToken);

    public async Task<SpProClockReading> ReadClockAsync(CancellationToken cancellationToken = default)
    {
        var words = await ReadWordsAsync(SpProRegisters.ClockRead, SpProClock.ReadWordCount, cancellationToken).ConfigureAwait(false);
        return new SpProClockReading(SpProClock.Decode(words, DateTime.Now.Year), SpProClock.DecodeDayOfWeek(words), words);
    }

    /// <summary>Sets the SP PRO clock. The inverter keeps local wall-clock time, so pass local time.</summary>
    public Task SetClockAsync(DateTime localTime, CancellationToken cancellationToken = default)
        => WriteWordsAsync(SpProRegisters.ClockWrite, SpProClock.Encode(localTime), cancellationToken);

    /// <summary>Tells the SP PRO that we are leaving, as SP LINK does on disconnect. Best effort.</summary>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_linkPort is not (1 or 2) || !NotifyOnDisconnect) return;
        try
        {
            var register = _linkPort == 1 ? SpProRegisters.Port1Disconnect : SpProRegisters.Port2Disconnect;
            await WriteWordsAsync(register, new ushort[] { 1 }, cancellationToken).ConfigureAwait(false);
        }
        catch (SpProException)
        {
            // Nothing useful to do if the goodbye is lost.
        }
        finally
        {
            _linkPort = -1;
            Connection = null;
        }
    }

    public ValueTask DisposeAsync() => Transport.DisposeAsync();
}
