namespace SpLink.Protocol;

/// <summary>Register addresses and tables recovered from SP LINK 16.11 that the clock feature depends on.</summary>
public static class SpProRegisters
{
    /// <summary>Which SP PRO comms port we are attached to (1 or 2). Reads as 0xFFFF until a login is accepted.</summary>
    public const uint LinkPort = 40960;        // 0xA000

    public const uint Port1BaudCode = 40965;
    public const uint Port2BaudCode = 40966;

    /// <summary>Writing 1 tells the SP PRO that SP LINK is disconnecting from that port.</summary>
    public const uint Port1Disconnect = 40973;
    public const uint Port2Disconnect = 40974;

    /// <summary>Read 8 words for the 16-byte login challenge; write 8 words with the MD5 response.</summary>
    public const uint LoginChallenge = 0x1F0000; // 2031616
    /// <summary>Reads 1 once the login response has been accepted.</summary>
    public const uint LoginResult = 0x1F0010;    // 2031632

    /// <summary>8 words: ms, sec, min, hour, day-of-week, day, month (+century in bit 7), year — BCD in the low byte.</summary>
    public const uint ClockRead = 0x1D0000;      // 1900544
    /// <summary>7 words: sec, min, hour, day-of-week, day, month (+century), year — same encoding, no milliseconds.</summary>
    public const uint ClockWrite = 0x1D0011;     // 1900561

    /// <summary>6 words of model-specific scaling: AC volts, AC current, DC volts, DC current, temperature, internal volts.</summary>
    public const uint CommonScaleFactors = 41000;

    /// <summary>85 words backing SP LINK's "Now" tab: SoC, battery, AC load, charger and generator state.</summary>
    public const uint NowBlock = 41048;
    public const int NowBlockWordCount = 85;

    /// <summary>
    /// The slices of the "Now" block that carry everything a load-shedding decision needs, so a
    /// fast path does not have to pull all 85 words.
    /// <para>
    /// Words 34-45 are a single run holding AC load power, charger and inverter status, AC source
    /// status, state of charge, DC current and battery current. Inverter AC power sits apart at
    /// 0-1. Together that is 14 registers in two round trips against 85 in one — worth having
    /// when the same reading is wanted every second rather than every twenty.
    /// </para>
    /// </summary>
    public static readonly (int Offset, int Count)[] FastWindows = [(0, 2), (34, 12)];

    /// <summary>4 words: model, 32-bit serial number, hardware revision.</summary>
    public const uint UnitInfo = 41053;

    /// <summary>68 words backing SP LINK's "Today" tab: energy accumulators and run hours.</summary>
    public const uint TodayBlock = 41135;
    public const int TodayBlockWordCount = 68;

    /// <summary>
    /// 24 words of status, of which word 2 is the memory map version. Several fields moved
    /// between versions, so decoders need it to pick the right word.
    /// </summary>
    public const uint ConfigStatus = 40967;
    public const int ConfigStatusWordCount = 8;
    public const int MemoryMapVersionOffset = 2;

    /// <summary>Registers that are part of the connection handshake rather than device settings.</summary>
    public static bool IsConnectionLifecycleRegister(uint address)
        => address is LoginChallenge or Port1Disconnect or Port2Disconnect;

    /// <summary>Baud rates SP LINK probes, in its order of preference.</summary>
    public static readonly int[] AutoBaudCandidates = [57600, 115200, 230400, 128000, 9600, 2400, 1200, 4800, 19200, 38400];

    public static string BaudCodeToString(int code) => code switch
    {
        0 => "1200",
        1 => "2400",
        2 => "4800",
        3 => "9600",
        4 => "19200",
        5 => "38400",
        6 => "57600",
        7 => "115200",
        8 => "128000",
        9 => "230400",
        10 => "Auto",
        _ => "Error",
    };
}
