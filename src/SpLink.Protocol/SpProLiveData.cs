namespace SpLink.Protocol;

/// <summary>
/// Model-specific scaling constants read from <see cref="SpProRegisters.CommonScaleFactors"/>.
/// Nearly every analogue reading is a raw count that must be multiplied by one of these.
/// </summary>
public sealed record SpProScaleFactors(short AcVolts, short AcCurrent, short DcVolts, short DcCurrent, short Temperature, short InternalVolts)
{
    public static SpProScaleFactors FromWords(ReadOnlySpan<ushort> words)
    {
        if (words.Length < 6)
            throw new ArgumentException($"expected 6 scale-factor words, got {words.Length}", nameof(words));
        return new SpProScaleFactors(
            (short)words[0], (short)words[1], (short)words[2],
            (short)words[3], (short)words[4], (short)words[5]);
    }

    public double ToDcVolts(ushort raw) => (short)raw * (DcVolts / 327680.0);
    public double ToDcAmps(int raw) => raw * (DcCurrent / 327680.0);
    public double ToAcVolts(ushort raw) => raw * (AcVolts / 327680.0);

    /// <summary>32-bit AC power counts to kW.</summary>
    public double ToAcKilowatts(int raw) => raw * (AcVolts * (AcCurrent / 26214400.0 / 1000.0));
}

/// <summary>A decoded snapshot of SP LINK's "Now" tab.</summary>
public sealed record SpProLiveReading
{
    /// <summary>Battery state of charge, or null when SoC control is disabled on the unit.</summary>
    public double? BatterySoCPercent { get; init; }
    public double BatteryVolts { get; init; }

    /// <summary>Battery current in amps; positive is charging, negative is discharging.</summary>
    public double BatteryAmps { get; init; }
    public double DcAmps { get; init; }
    public double AcLoadKilowatts { get; init; }

    /// <summary>The pre-memory-map-v20 interpretation of AC load. Verified to mirror <see cref="AcLoadKilowatts"/> on current firmware.</summary>
    public double AcLoadKilowattsLegacy { get; init; }
    public double AcVolts { get; init; }
    public double AcFrequencyHz { get; init; }
    public string ChargerState { get; init; } = "";
    public string GeneratorState { get; init; } = "";
    public string GeneratorReason { get; init; } = "";

    /// <summary>Battery power in kW, derived from volts x amps.</summary>
    public double BatteryKilowatts => BatteryVolts * BatteryAmps / 1000.0;

    public ushort[] RawWords { get; init; } = [];
}

/// <summary>Decodes the "Now" register block. Index and scaling rules recovered from SP LINK's subDisplayData_NowSubTab.</summary>
public static class SpProLiveData
{
    // Word offsets within the 85-word block read from SpProRegisters.NowBlock.
    private const int AcLoadPowerLo = 34;      // memory map v20+
    private const int ChargerStatus = 36;
    private const int GeneratorRunReason = 39;
    private const int SoC = 41;
    private const int DcCurrent = 43;
    private const int BatteryCurrentLo = 44;   // 32-bit
    private const int AcVoltsRms = 56;
    private const int AcFrequency = 58;
    private const int AcLoadPowerLegacyLo = 59;
    private const int BatteryVolts = 4;
    private const int GeneratorStatus = 62;

    public static SpProLiveReading Decode(ReadOnlySpan<ushort> w, SpProScaleFactors scale)
    {
        if (w.Length < SpProRegisters.NowBlockWordCount)
            throw new ArgumentException($"expected {SpProRegisters.NowBlockWordCount} words, got {w.Length}", nameof(w));

        return new SpProLiveReading
        {
            // 0xFFFF is SP LINK's "SoC control disabled" sentinel; otherwise the count is percent x 256.
            BatterySoCPercent = w[SoC] == ushort.MaxValue ? null : w[SoC] / 256.0,
            BatteryVolts = scale.ToDcVolts(w[BatteryVolts]),
            BatteryAmps = scale.ToDcAmps(Int32At(w, BatteryCurrentLo)),
            DcAmps = scale.ToDcAmps((short)w[DcCurrent]),
            AcLoadKilowatts = scale.ToAcKilowatts(Int32At(w, AcLoadPowerLo)),
            AcLoadKilowattsLegacy = scale.ToAcKilowatts(Int32At(w, AcLoadPowerLegacyLo)),
            AcVolts = scale.ToAcVolts(w[AcVoltsRms]),
            AcFrequencyHz = (short)w[AcFrequency] / 100.0,
            ChargerState = ChargerStateName(w[ChargerStatus]),
            GeneratorState = GeneratorStatusName(w[GeneratorStatus]),
            GeneratorReason = GeneratorReasonName(w[GeneratorRunReason]),
            RawWords = w.ToArray(),
        };
    }

    private static int Int32At(ReadOnlySpan<ushort> w, int loIndex) => w[loIndex] | (w[loIndex + 1] << 16);

    public static string ChargerStateName(ushort value) => value switch
    {
        0 => "Initial",
        1 => "Bulk",
        2 => "Absorb",
        3 => "Short Term Float",
        4 => "Return to Float",
        5 => "Equalise",
        6 => "Long Term Float",
        _ => $"Unknown ({value})",
    };

    public static string GeneratorStatusName(ushort value) => value switch
    {
        0 => "Not Running",
        1 => "Running",
        2 => "Low Fuel",
        3 => "No Fuel",
        4 => "Fault",
        5 => "Not Available",
        6 => "Starting",
        7 => "Retry Pause",
        8 => "Stopping",
        9 => "Disabled",
        10 => "AC Source Present",
        _ => $"Unknown ({value})",
    };

    public static string GeneratorReasonName(ushort value) => value switch
    {
        0 => "Not Running",
        1 => "Front Panel",
        2 => "Remote Run Request",
        3 => "Run Schedule",
        4 => "Hi Inverter Temp.",
        5 => "Impending Inverter Shutdown",
        6 => "Synchronisation Fault",
        7 => "State of Charge",
        8 => "Low Battery Volts",
        9 => "Battery Mid Point Voltage Error",
        10 => "Equalising Battery",
        11 => "Hi AC Load",
        12 => "Generator Exercise",
        13 => "Generator Available",
        14 => "Generator Fault",
        15 => "Minimum Runtime",
        16 => "Generator Lock Out Active",
        17 => "Battery Float",
        18 => "Cooling Down",
        19 => "Confirmed Start",
        20 => "Manual",
        21 => "AC Source Present",
        22 => "Disabled",
        23 => "Support Mode",
        24 => "Equalise",
        _ => $"Unknown ({value})",
    };
}
