using System.Buffers.Binary;
using System.Threading.Channels;
using SpLink.Protocol.Transport;

namespace SpLink.Protocol.Simulation;

/// <summary>
/// An in-memory SP PRO that speaks just enough of the protocol for connect, login and clock operations.
/// Used by the tests and by the CLI's --simulate mode so the full flow can run without hardware.
/// </summary>
public sealed class FakeSpPro : ISpProTransport
{
    private readonly Channel<byte[]> _toHost = Channel.CreateUnbounded<byte[]>();
    private byte[] _pending = [];
    private int _pendingOffset;
    private DateTime _fixedClock;
    private TimeSpan _clockOffset;

    public FakeSpPro(DateTime? initialClock = null, bool ticksInRealTime = true)
    {
        TicksInRealTime = ticksInRealTime;
        Clock = initialClock ?? DateTime.Now;
    }

    public string Description => "simulated SP PRO";
    public string Password { get; set; } = SpProLogin.DefaultPassword;
    public bool RequireLogin { get; set; } = true;
    public bool LoggedIn { get; private set; }
    public int LinkPort { get; set; } = 1;
    public int BaudCode { get; set; } = 7; // 115200
    public byte[] Challenge { get; set; } = Enumerable.Range(0, SpProLogin.ChallengeLength).Select(i => (byte)(0xA5 ^ (i * 17))).ToArray();

    /// <summary>Bytes emitted before every response, to exercise receiver resynchronisation.</summary>
    public byte[] NoisePrefix { get; set; } = [];

    /// <summary>Model index, serial number and hardware revision reported from the unit-info register.</summary>
    public int ModelIndex { get; set; }
    public uint SerialNumber { get; set; } = 100001;
    public ushort HardwareRevision { get; set; } = 25;

    /// <summary>Values served from configuration registers, keyed by absolute address.</summary>
    public Dictionary<uint, ushort> ConfigWords { get; } = new();
    /// <summary>Zero the first two header bytes of link-port responses, as some network gateways do.</summary>
    public bool MangleLinkPortHeader { get; set; }
    public bool DisconnectRequested { get; private set; }
    public List<byte[]> RequestLog { get; } = new();

    /// <summary>When true the simulated clock advances with real time; when false it stays where it was set.</summary>
    public bool TicksInRealTime { get; }

    public DateTime Clock
    {
        get => TicksInRealTime ? DateTime.Now + _clockOffset : _fixedClock;
        set
        {
            if (TicksInRealTime) _clockOffset = value - DateTime.Now;
            else _fixedClock = value;
        }
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var request = data.ToArray();
        RequestLog.Add(request);
        // A real unit stays silent on a corrupt frame; the host's retry logic deals with it.
        if (!SpProFrame.TryValidateRequest(request, out _)) return ValueTask.CompletedTask;

        var response = Handle(request);
        _toHost.Writer.TryWrite(NoisePrefix.Length == 0 ? response : [.. NoisePrefix, .. response]);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_pendingOffset >= _pending.Length)
        {
            _pending = await _toHost.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            _pendingOffset = 0;
        }
        int count = Math.Min(buffer.Length, _pending.Length - _pendingOffset);
        _pending.AsSpan(_pendingOffset, count).CopyTo(buffer.Span);
        _pendingOffset += count;
        return count;
    }

    public ValueTask DiscardInputAsync(CancellationToken cancellationToken = default)
    {
        _pending = [];
        _pendingOffset = 0;
        while (_toHost.Reader.TryRead(out _)) { }
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _toHost.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private byte[] Handle(byte[] request)
    {
        uint address = SpProFrame.Address(request);
        int count = SpProFrame.WordCount(request);

        if (SpProFrame.Op(request) == FrameOp.Write)
        {
            var words = SpProFrame.DataWords(request);
            switch (address)
            {
                case SpProRegisters.LoginChallenge:
                    LoggedIn = words.AsSpan().SequenceEqual(SpProLogin.ComputeResponseWords(Challenge, Password));
                    break;
                case SpProRegisters.ClockWrite when words.Length == SpProClock.WriteWordCount:
                    Clock = SpProClock.Decode([0, .. words], DateTime.Now.Year);
                    break;
                case SpProRegisters.Port1Disconnect:
                case SpProRegisters.Port2Disconnect:
                    DisconnectRequested = true;
                    LoggedIn = false;
                    break;
            }
            return request; // write responses echo the request
        }

        var data = new ushort[count];
        switch (address)
        {
            case SpProRegisters.LinkPort:
                data[0] = (ushort)(RequireLogin && !LoggedIn ? 0xFFFF : LinkPort);
                break;
            case SpProRegisters.LoginChallenge:
                for (int i = 0; i < Math.Min(count, Challenge.Length / 2); i++)
                    data[i] = BinaryPrimitives.ReadUInt16LittleEndian(Challenge.AsSpan(i * 2, 2));
                break;
            case SpProRegisters.LoginResult:
                data[0] = (ushort)(LoggedIn ? 1 : 0);
                break;
            case SpProRegisters.Port1BaudCode:
            case SpProRegisters.Port2BaudCode:
                data[0] = (ushort)BaudCode;
                break;
            case SpProRegisters.UnitInfo:
                if (count >= 4)
                {
                    data[0] = (ushort)ModelIndex;
                    data[1] = (ushort)SerialNumber;
                    data[2] = (ushort)(SerialNumber >> 16);
                    data[3] = HardwareRevision;
                }
                break;
            case SpProRegisters.CommonScaleFactors:
                ushort[] scales = [5300, 2934, 1050, 16000, 530, 1000];
                for (int i = 0; i < Math.Min(count, scales.Length); i++) data[i] = scales[i];
                break;
            case SpProRegisters.ClockRead:
            {
                var now = Clock;
                var encoded = SpProClock.Encode(now);
                data[0] = (ushort)(((now.Millisecond / 100) << 4) | (now.Millisecond % 100 / 10));
                for (int i = 0; i < Math.Min(count - 1, encoded.Length); i++)
                    data[i + 1] = encoded[i];
                break;
            }
        }

        for (int i = 0; i < count; i++)
            if (ConfigWords.TryGetValue(address + (uint)i, out var configured)) data[i] = configured;

        var response = new byte[SpProFrame.ResponseLength(count)];
        request.AsSpan(0, SpProFrame.HeaderLength).CopyTo(response);
        for (int i = 0; i < count; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(SpProFrame.HeaderLength + i * 2, 2), data[i]);
        Crc16Kermit.WriteCrc(response.AsSpan(SpProFrame.HeaderLength, count * 2), response.AsSpan(SpProFrame.HeaderLength + count * 2, 2));
        if (MangleLinkPortHeader && address == SpProRegisters.LinkPort) { response[0] = 0; response[1] = 0; }
        return response;
    }
}
