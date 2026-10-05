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

    // --- AC-coupled solar, as the SP PRO measures it -------------------------------------
    /// <summary>
    /// Total AC-coupled (grid-tied) solar power in kW. SP LINK calls these "Kaco" readings after the
    /// brand it first supported; they cover any managed AC-coupled inverter. This is the SP PRO's own
    /// measurement, independent of anything the solar inverters report over their own network.
    /// </summary>
    public double AcCoupledKilowatts { get; init; }

    /// <summary>Per-inverter AC-coupled power in kW, up to the five slots the SP PRO tracks.</summary>
    public double[] AcCoupledKilowattsPerInverter { get; init; } = [];

    /// <summary>AC-coupled output as a percentage, as SP LINK reports it.</summary>
    public double AcCoupledPercent { get; init; }

    // --- inverter ------------------------------------------------------------------------
    /// <summary>The inverter's own AC power in kW, which is not the same as the load figure.</summary>
    public double InverterAcKilowatts { get; init; }
    public double InverterAcAmps { get; init; }
    public string InverterMode { get; init; } = "";
    public string AcSourceStatus { get; init; } = "";

    // --- AC source / generator -----------------------------------------------------------
    public double GeneratorKilowatts { get; init; }
    public double GeneratorKilowatts5MinAverage { get; init; }
    public double GeneratorVolts { get; init; }
    public double GeneratorAmps { get; init; }
    public double GeneratorFrequencyHz { get; init; }
    public double MaxAvailableInputKilowatts { get; init; }

    // --- battery trends ------------------------------------------------------------------
    public double BatteryLoad5MinKilowatts { get; init; }
    public double BatteryLoad15MinKilowatts { get; init; }

    // --- DC shunts (null unless a shunt is configured) ------------------------------------
    public double Shunt1Amps { get; init; }
    public double Shunt2Amps { get; init; }
    public double Shunt1Kilowatts { get; init; }
    public double Shunt2Kilowatts { get; init; }

    // --- system regulation ----------------------------------------------------------------
    public int ActiveSchedule { get; init; }
    public double InputPowerLimitKilowatts { get; init; }
    public double ExportPowerLimitKilowatts { get; init; }
    public double ChargePowerLimitKilowatts { get; init; }
    public double SupportPowerLimitKilowatts { get; init; }
    public string RegulationChargerStatus { get; init; } = "";
    public string InverterLockoutStatus { get; init; } = "";
    public string SourceDisconnectStatus { get; init; } = "";

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
    private const int InverterAcPowerLo = 0;      // 32-bit
    private const int ActiveSchedule = 24;
    private const int InputPowerLimit = 25;
    private const int ExportPowerLimit = 26;
    private const int ChargePowerLimit = 27;
    private const int SupportPowerLimit = 28;
    private const int RegulationChargerStatus = 29;
    private const int InverterLockoutStatus = 30;
    private const int SourceDisconnectStatus = 31;
    private const int InverterMode = 37;
    private const int GeneratorStartReason = 38;
    private const int AcSourceStatus = 40;
    private const int Shunt1Amps = 46;
    private const int Shunt2Amps = 47;
    private const int Shunt1Power = 48;
    private const int Shunt2Power = 49;
    private const int GeneratorPower5Min = 51;
    private const int GeneratorAmps = 52;
    private const int GeneratorFrequency = 53;
    private const int MaxAvailableInputLo = 54;   // 32-bit
    private const int AcInverterRmsAmps = 57;
    private const int GeneratorVolts = 61;
    private const int BatteryLoad5MinLo = 63;     // 32-bit
    private const int BatteryLoad15MinLo = 65;    // 32-bit
    private const int AcCoupledPerInverter = 68;  // five consecutive slots
    private const int AcCoupledPercentLo = 73;
    private const int AcCoupledTotalLegacy = 67;
    private const int GeneratorPowerV20 = 83;     // memory map 20+; word 50 is the legacy slot
    private const int AcCoupledTotalV20 = 84;     // memory map 20+; word 67 is the legacy slot

    public static SpProLiveReading Decode(ReadOnlySpan<ushort> w, SpProScaleFactors scale)
    {
        if (w.Length < SpProRegisters.NowBlockWordCount)
            throw new ArgumentException($"expected {SpProRegisters.NowBlockWordCount} words, got {w.Length}", nameof(w));

        // A span cannot be captured, so gather the per-inverter slots before the initializer.
        var perInverter = new double[5];
        for (var i = 0; i < perInverter.Length; i++)
            perInverter[i] = scale.AcKilowatts16Unsigned(w[AcCoupledPerInverter + i]);

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

            // SP LINK reads the total from word 84 on memory map 20+ and word 67 before it. Every
            // SP PRO this tool has met reports 33, and the two words have always agreed, so prefer
            // the modern slot and fall back only when it is empty.
            AcCoupledKilowatts = scale.AcKilowatts16Unsigned(
                w[AcCoupledTotalV20] != 0 ? w[AcCoupledTotalV20] : w[AcCoupledTotalLegacy]),
            AcCoupledKilowattsPerInverter = perInverter,
            AcCoupledPercent = SpProScaling.Deci(w[AcCoupledPercentLo]),

            InverterAcKilowatts = scale.AcKilowatts32(w[InverterAcPowerLo], w[InverterAcPowerLo + 1]),
            InverterAcAmps = scale.AcAmps(w[AcInverterRmsAmps]),
            InverterMode = InverterModeName(w[InverterMode]),
            AcSourceStatus = AcSourceStatusName(w[AcSourceStatus]),

            GeneratorKilowatts = scale.AcKilowatts16Displayed(w[GeneratorPowerV20]),
            GeneratorKilowatts5MinAverage = scale.AcKilowatts16Displayed(w[GeneratorPower5Min]),
            GeneratorVolts = scale.AcVolts(w[GeneratorVolts]),
            GeneratorAmps = scale.AcAmps(w[GeneratorAmps]),
            GeneratorFrequencyHz = SpProScaling.Hertz(w[GeneratorFrequency]),
            MaxAvailableInputKilowatts = scale.AcKilowatts32(w[MaxAvailableInputLo], w[MaxAvailableInputLo + 1]),

            BatteryLoad5MinKilowatts = scale.DcKilowatts32(w[BatteryLoad5MinLo], w[BatteryLoad5MinLo + 1]),
            BatteryLoad15MinKilowatts = scale.DcKilowatts32(w[BatteryLoad15MinLo], w[BatteryLoad15MinLo + 1]),

            Shunt1Amps = scale.DcAmps(w[Shunt1Amps]),
            Shunt2Amps = scale.DcAmps(w[Shunt2Amps]),
            Shunt1Kilowatts = scale.DcKilowatts16(w[Shunt1Power]),
            Shunt2Kilowatts = scale.DcKilowatts16(w[Shunt2Power]),

            ActiveSchedule = w[ActiveSchedule],
            InputPowerLimitKilowatts = scale.AcKilowatts16Displayed(w[InputPowerLimit]),
            ExportPowerLimitKilowatts = scale.AcKilowatts16Displayed(w[ExportPowerLimit]),
            ChargePowerLimitKilowatts = scale.AcKilowatts16Displayed(w[ChargePowerLimit]),
            SupportPowerLimitKilowatts = scale.AcKilowatts16Displayed(w[SupportPowerLimit]),
            RegulationChargerStatus = ChargerLockoutName(w[RegulationChargerStatus]),
            InverterLockoutStatus = ChargerLockoutName(w[InverterLockoutStatus]),
            SourceDisconnectStatus = ChargerLockoutName(w[SourceDisconnectStatus]),

            RawWords = w.ToArray(),
        };
    }

    private static int Int32At(ReadOnlySpan<ushort> w, int loIndex) => w[loIndex] | (w[loIndex + 1] << 16);

    public static string InverterModeName(ushort value) => value switch
    {
        0 => "Idle",
        1 => "Econo",
        2 => "On",
        3 => "Sync",
        _ => $"Unknown ({value})",
    };

    /// <summary>
    /// The settings-version-22 table. Codes 2-5 and 7 all collapse to "in tolerance" there, where
    /// older firmware distinguished lockout and capacity-limit states.
    /// </summary>
    public static string AcSourceStatusName(ushort value) => value switch
    {
        0 => "AC Source Not Present",
        1 => "E-N Link Not Detected",
        2 or 3 or 4 or 5 or 7 => "AC Source in Tolerance",
        6 => "Outside operating range",
        8 => "Volts too high for freq",
        9 => "Disconnected by DRM 0",
        _ => $"Unknown ({value})",
    };

    public static string ChargerLockoutName(ushort value) => value switch
    {
        0 => "Not locked out",
        1 => "Lockout Requested",
        _ => $"Unknown ({value})",
    };

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
