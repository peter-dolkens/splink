namespace SpLink.Protocol;

/// <summary>Scale factors that apply to one log record. Most records carry their own in trailing words.</summary>
public sealed record SpProLogScales(int AcVolts, int AcCurrent, int DcVolts, int DcCurrent, int Temperature)
{
    /// <summary>Detailed records keep five scales in the last five words.</summary>
    public static SpProLogScales FromTail(ushort[] w, int offsetFromEnd = 5) =>
        new(w[^offsetFromEnd], w[^(offsetFromEnd - 1)], w[^(offsetFromEnd - 2)], w[^(offsetFromEnd - 3)], w[^(offsetFromEnd - 4)]);

    public static SpProLogScales FromCommon(SpProScaleFactors f) =>
        new(f.AcVolts, f.AcCurrent, f.DcVolts, f.DcCurrent, f.Temperature);
}

/// <summary>
/// One decoded field. <see cref="Value"/> is null when the inverter recorded a blank/fault sentinel.
/// Enumerated fields also carry SP LINK's <see cref="Text"/> label.
/// </summary>
public sealed record LogField(string Name, double? Value, string Unit = "", string? Text = null)
{
    public string Display => Text ?? Value?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "";
}

public sealed record DecodedLogRecord(SpProLogType Type, DateTime Timestamp, uint Address, IReadOnlyList<LogField> Fields)
{
    public double? this[string name] => Fields.FirstOrDefault(f => f.Name == name)?.Value;
}

/// <summary>
/// Decodes logged records into engineering units, following SP LINK's CSV path (FormatForDisplay: false),
/// which omits the sign flips and absolute values the GUI applies for presentation.
/// </summary>
public static class SpProLogDecoder
{
    /// <summary>Seconds since this epoch. SP LINK's function is named "Year2000" but builds 2001-01-01.</summary>
    public static readonly DateTime Epoch = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    public static DateTime Timestamp(ushort lo, ushort hi) => Epoch.AddSeconds((uint)(lo | (hi << 16)));

    private static short I16(ushort v) => unchecked((short)v);
    private static int I32(ushort lo, ushort hi) => unchecked((int)(uint)(lo | (hi << 16)));
    private static uint U32(ushort lo, ushort hi) => (uint)(lo | (hi << 16));

    private static double DcVolts(ushort v, SpProLogScales s) => I16(v) * (s.DcVolts / 327680.0);
    private static double AcVolts(ushort v, SpProLogScales s) => I16(v) * (s.AcVolts / 327680.0);
    private static double DcAmps(ushort v, SpProLogScales s) => I16(v) * (s.DcCurrent / 327680.0);
    private static double AcKw16(ushort v, SpProLogScales s) => I16(v) * s.AcVolts * s.AcCurrent / 3276800.0 / 1000.0;
    private static double AcKw32(ushort lo, ushort hi, SpProLogScales s) => I32(lo, hi) * s.AcVolts * s.AcCurrent / 26214400.0 / 1000.0;
    private static double Kwh32U(ushort lo, ushort hi, int sv, int si) => U32(lo, hi) * 24.0 * sv * si / 3276800.0 / 1000.0;
    private static double Kwh32S(ushort lo, ushort hi, int sv, int si) => I32(lo, hi) * 24.0 * sv * si / 3276800.0 / 1000.0;
    private static double Kwh16U(ushort v, int sv, int si) => v * 24.0 * sv * si / 3276800.0 / 1000.0;
    private static double Kwh16S(ushort v, int sv, int si) => I16(v) * 24.0 * sv * si / 3276800.0 / 1000.0;
    private static double Hours(ushort v) => v / 60.0;
    private static double Hours32(ushort lo, ushort hi) => U32(lo, hi) / 60.0;
    private static double Percent(ushort v) => v / 256.0;

    /// <summary>0x7FFF marks a sensor fault.</summary>
    private static double? Temperature(ushort v, SpProLogScales s) => v == 0x7FFF ? null : I16(v) * (s.Temperature / 32768.0);
    /// <summary>0xFFFF marks SoC monitoring disabled.</summary>
    private static double? Soc(ushort v) => v == 0xFFFF ? null : v / 256.0;

    public static DecodedLogRecord Decode(SpProLogRecord record, SpProLogFormatVersions versions, SpProScaleFactors common) =>
        record.Type switch
        {
            SpProLogType.DailySummary => DecodeDailySummary(record, versions.DailySummary),
            SpProLogType.Detailed => DecodeDetailed(record, versions.Detailed),
            _ => DecodeEvent(record, versions.Event, common),
        };

    public static DecodedLogRecord DecodeDailySummary(SpProLogRecord r, int version)
    {
        var w = r.Words;
        // Daily summary keeps its scales one word further from the end; the final word is unused.
        var s = SpProLogScales.FromTail(w, 6);
        var f = new List<LogField>
        {
            new("DcInputTotal", Kwh32U(w[2], w[3], s.DcVolts, s.DcCurrent), "kWh"),
            new("DcOutputTotal", Kwh32U(w[4], w[5], s.DcVolts, s.DcCurrent), "kWh"),
            new("BatteryInTotal", Kwh32U(w[6], w[7], s.DcVolts, s.DcCurrent), "kWh"),
            new("BatteryOutTotal", Kwh32U(w[8], w[9], s.DcVolts, s.DcCurrent), "kWh"),
            new("AcInputTotal", Kwh32U(w[10], w[11], s.AcVolts, s.AcCurrent), "kWh"),
            new("AcLoadTotal", Kwh32U(w[12], w[13], s.AcVolts, s.AcCurrent), "kWh"),
            new("Shunt1Total", Kwh32S(w[14], w[15], s.DcVolts, s.DcCurrent), "kWh"),
            new("Shunt2Total", Kwh32S(w[16], w[17], s.DcVolts, s.DcCurrent), "kWh"),
            new("AcInputHours", Hours32(w[18], w[19]), "h"),
            new("AcExportTotal", Kwh32U(w[20], w[21], s.AcVolts, s.AcCurrent), "kWh"),
            new("InverterDcTotal", Kwh32S(w[22], w[23], s.DcVolts, s.DcCurrent), "kWh"),
            new("ModulationHours", Hours32(w[24], w[25]), "h"),
            new("FloatHours", Hours(w[26]), "h"),
            new("BatteryChargeEfficiencyIndex", w[29] / 2048.0),
        };

        if (version >= 5)
            // Words 27/28 become one 32-bit total with the halves swapped relative to every other field here.
            f.Add(new LogField("AcCoupledTotal",
                w[27] == 0xFFFF && w[28] == 0xFFFF ? null : Kwh32U(w[28], w[27], s.AcVolts, s.AcCurrent), "kWh"));
        else
        {
            f.Add(new LogField("Shunt1PeakPower", I16(w[27]) * s.DcVolts * s.DcCurrent / 3276800.0 / 1000.0, "kW"));
            f.Add(new LogField("Shunt2PeakPower", I16(w[28]) * s.DcVolts * s.DcCurrent / 3276800.0 / 1000.0, "kW"));
        }
        return new DecodedLogRecord(r.Type, Timestamp(w[0], w[1]), r.Address, f);
    }

    public static DecodedLogRecord DecodeDetailed(SpProLogRecord r, int version)
    {
        var w = r.Words;
        var s = SpProLogScales.FromTail(w);
        var f = new List<LogField>
        {
            new("InverterAcPowerAvg", AcKw32(w[2], w[3], s), "kW"),
            new("DcInputAccum", Kwh32U(w[4], w[5], s.DcVolts, s.DcCurrent), "kWh"),
            new("DcOutputAccum", Kwh32U(w[6], w[7], s.DcVolts, s.DcCurrent), "kWh"),
            new("BatteryInAccum", Kwh32U(w[8], w[9], s.DcVolts, s.DcCurrent), "kWh"),
            new("BatteryOutAccum", Kwh32U(w[10], w[11], s.DcVolts, s.DcCurrent), "kWh"),
            new("DcVoltageAvg", DcVolts(w[12], s), "V"),
            new("DcVoltageMin", DcVolts(w[13], s), "V"),
            new("DcVoltageMax", DcVolts(w[14], s), "V"),
            new("DcMidVoltageAvg", DcVolts(w[15], s), "V"),
            new("DcMidVoltageAtMinDc", DcVolts(w[16], s), "V"),
            new("DcMidVoltageAtMaxDc", DcVolts(w[17], s), "V"),
            new("InverterDcCurrentAvg", DcAmps(w[18], s), "A"),
            new("Shunt1CurrentAvg", DcAmps(w[19], s), "A"),
            new("Shunt2CurrentAvg", DcAmps(w[20], s), "A"),
            new("LoadAcPowerAvg", AcKw16(w[21], s), "kW"),
            new("LoadAcPowerMax", AcKw16(w[22], s), "kW"),
            new("AcInputPowerAvg", AcKw16(w[23], s), "kW"),
            new("AcLoadVoltageAvg", AcVolts(w[24], s), "V"),
            new("AcLoadFrequencyAvg", I16(w[25]) / 100.0, "Hz"),
            new("TransformerTempMax", Temperature(w[26], s), "°C"),
            new("HeatsinkTempMax", Temperature(w[27], s), "°C"),
            new("BatteryTempMax", Temperature(w[28], s), "°C"),
            new("StateOfCharge", Soc(w[31]), "%"),
            new("AcInputAccum", Kwh16U(w[32], s.AcVolts, s.AcCurrent), "kWh"),
            new("AcLoadAccum", Kwh16U(w[33], s.AcVolts, s.AcCurrent), "kWh"),
            new("Shunt1Accum", Kwh16S(w[34], s.DcVolts, s.DcCurrent), "kWh"),
            new("Shunt2Accum", Kwh16S(w[35], s.DcVolts, s.DcCurrent), "kWh"),
            new("AnalogueIn1Voltage", DcVolts(w[36], s), "V"),
            new("AnalogueIn2Voltage", DcVolts(w[37], s), "V"),
            new("AcExportAccum", Kwh16U(w[38], s.AcVolts, s.AcCurrent), "kWh"),
        };

        if (version >= 3)
        {
            // Words 29/30 are repurposed for AC-coupled (Kaco) totals on v3+.
            f.Add(new LogField("AcCoupledPower", w[29] == 0xFFFF ? null : AcKw16(w[29], s), "kW"));
            f.Add(new LogField("AcCoupledEnergy", w[30] == 0xFFFF ? null : Kwh16U(w[30], s.AcVolts, s.AcCurrent), "kWh"));
        }
        else
        {
            f.Add(new LogField("InternalTempMax", Temperature(w[29], s), "°C"));
            f.Add(new LogField("PowerModuleTempMax", Temperature(w[30], s), "°C"));
        }
        return new DecodedLogRecord(r.Type, Timestamp(w[0], w[1]), r.Address, f);
    }

    /// <summary>0xFFFF in the descriptor word marks an unwritten slot, which SP LINK discards.</summary>
    public static bool IsBlankEvent(SpProLogRecord r) => r.Words[2] == 0xFFFF;

    public static DecodedLogRecord DecodeEvent(SpProLogRecord r, int version, SpProScaleFactors common)
    {
        var w = r.Words;
        // From v3 the record stops carrying most scales; only AC current stays embedded, at a fixed index.
        var s = version >= 3
            ? new SpProLogScales(common.AcVolts, w[32], common.DcVolts, common.DcCurrent, common.Temperature)
            : SpProLogScales.FromTail(w);

        var f = new List<LogField>
        {
            new("Event", w[2], "", SpProLogLabels.Lookup(
                r.Type == SpProLogType.AlertEvents ? "AlertEvent" : "OperationalEvent", w[2]) ?? $"Code {w[2]}"),
            new("DcVoltage", DcVolts(w[3], s), "V"),
            new("DcMidVoltage", DcVolts(w[4], s), "V"),
            new("InverterDcCurrent", DcAmps(w[5], s), "A"),
            new("Shunt1Current", DcAmps(w[6], s), "A"),
            new("Shunt2Current", DcAmps(w[7], s), "A"),
            new("LoadAcPower", AcKw32(w[8], w[9], s), "kW"),
            new("InverterAcPower", AcKw32(w[10], w[11], s), "kW"),
            new("AcInputPower", AcKw16(w[12], s), "kW"),
            new("AcLoadVoltage", AcVolts(w[13], s), "V"),
            new("StateOfCharge", Soc(w[14]), "%"),
            new("AcLoadFrequency", I16(w[15]) / 100.0, "Hz"),
            new("TransformerTemp", Temperature(w[16], s), "°C"),
            new("Heatsink1Temp", Temperature(w[17], s), "°C"),
            new("Heatsink2Temp", Temperature(w[18], s), "°C"),
            new("BatteryTemp", Temperature(w[19], s), "°C"),
            new("InternalTemp", Temperature(w[20], s), "°C"),
            new("AmbientTemp", Temperature(w[21], s), "°C"),
            new("OperationalMode", w[22], "", SpProLogLabels.Lookup("OperationalMode", w[22])),
            new("BatteryChargeMode", w[23], "", SpProLogLabels.Lookup("BatteryChargeMode", w[23])),
            new("ContactorState", w[24], "", SpProLogLabels.Lookup("ContactorState", w[24])),
            new("GeneratorStatus", w[25], "", SpProLogLabels.Lookup("GeneratorStatus", w[25])),
            new("GeneratorStartReason", w[26], "", SpProLogLabels.Lookup("GeneratorReason", w[26])),
            new("GeneratorRunningReason", w[27], "", SpProLogLabels.Lookup("GeneratorReason", w[27])),
            new("DcBatteryCableLoss", w[28] * s.DcVolts * s.DcCurrent / 3276800.0, "W"),
            new("FanSpeed", Percent(w[29]), "%"),
        };

        if (version >= 3)
        {
            // Word 31 doubles as a selector for word 30 and as the discharge-current target.
            if (w[31] == 1) f.Add(new LogField("BatteryChargeEfficiencyIndex", w[30] / 2048.0));
            else f.Add(new LogField("BmsStateOfHealth", Percent(w[30]), "%"));
            f.Add(new LogField("DischargeCurrentTarget", I16(w[31]) * s.DcCurrent / 40960.0, "A"));
            f.Add(new LogField("ChargeCurrentTarget", I16(w[33]) * s.DcCurrent / 40960.0, "A"));
        }
        else
        {
            f.Add(new LogField("BatteryChargeEfficiencyIndex", w[30] / 2048.0));
        }
        return new DecodedLogRecord(r.Type, Timestamp(w[0], w[1]), r.Address, f);
    }
}
