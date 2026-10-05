namespace SpLink.Protocol;

/// <summary>A decoded field: a value where the converter is known, the raw words either way.</summary>
public sealed record SpProFieldValue(string Name, string Converter, int[] Words, ushort[] Raw)
{
    /// <summary>The engineering value, or null when the converter is not implemented.</summary>
    public object? Value { get; init; }

    /// <summary>The unit, where the converter implies one.</summary>
    public string? Unit { get; init; }

    /// <summary>True when the value was produced by a converter rather than left raw.</summary>
    public bool Decoded => Value is not null;
}

/// <summary>A decoded register block.</summary>
public sealed record SpProBlockReading(string Name, uint Address, DateTimeOffset ReadAt,
                                       IReadOnlyList<SpProFieldValue> Fields, ushort[] RawWords);

/// <summary>
/// Applies SP LINK's converters to the generated block map.
/// <para>
/// The conversions here are transcribed from mDataConvert. Where a converter has not been
/// transcribed the field is still emitted with its raw words and the converter's name, because
/// an undecoded register that says so is useful, whereas one quietly dropped or guessed at is not.
/// </para>
/// </summary>
public static class SpProDisplayDecoder
{
    public static SpProBlockReading Decode(SpProDisplayBlocks.Block block, ushort[] words, SpProScaleFactors scale)
    {
        var fields = new List<SpProFieldValue>(block.Fields.Length);
        foreach (var f in block.Fields)
        {
            if (f.Words.Any(i => i >= words.Length)) continue;
            var raw = f.Words.Select(i => words[i]).ToArray();
            var (value, unit) = Convert(f.Converter, raw, scale, f.Args);
            fields.Add(new SpProFieldValue(f.Name, f.Converter, f.Words, raw) { Value = value, Unit = unit });
        }
        return new SpProBlockReading(block.Name, block.Address, DateTimeOffset.Now, fields, words);
    }

    private static (object? Value, string? Unit) Convert(string converter, ushort[] r, SpProScaleFactors s, int[] args)
    {
        // Several converters take a 32-bit pair; guard so a short field cannot index past its words.
        ushort Lo() => r[0];
        ushort Hi() => r.Length > 1 ? r[1] : (ushort)0;

        return converter switch
        {
            // --- AC power -------------------------------------------------------------------
            "ConvertSignedACPowerValueToString" => (Round(s.AcKilowatts16Displayed(Lo()), 3), "kW"),
            "ConvertSignedACPowerValueToStringAndRemoveSign" => (Round(s.AcKilowatts16Unsigned(Lo()), 3), "kW"),
            "Convert32bitSignedACPowerValueToString"
                or "Convert32bitSignedACPowerValueToStringUsingRnDSignConv"
                or "Convert32bitSignedACPowerValueToStringWithOptionalTimeConstant"
                => (Round(s.AcKilowatts32(Lo(), Hi()), 3), "kW"),
            "ConvertSignedACPowerValueInDecaWattsToString" => (Round(SpProScaling.Signed(Lo()) / 100.0, 3), "kW"),
            "ConvertSignedACReactivePowerValueToString" => (Round(s.AcKilowatts16(Lo()), 3), "kvar"),
            "ConvertSignedACApparentPowerValueToString" => (Round(s.AcKilowatts16(Lo()), 3), "kVA"),

            // --- AC energy, volts, current, frequency ---------------------------------------
            "ConvertUnSignedACEnergyValueInkWhToString" => (Round(s.AcKilowattHours(Lo()), 3), "kWh"),
            "Convert32BitUnSignedACEnergyValueInkWhToString" => (Round(s.AcKilowattHours32(Lo(), Hi()), 3), "kWh"),
            "ConvertUnSignedACVoltageValueToString"
                or "ConvertUnSignedACLoadVoltageValueClampedToSourceVoltageInSyncToString"
                => (Round(s.AcVolts(Lo()), 1), "V"),
            "ConvertSignedACVoltageValueToString" => (Round(s.AcVoltsSigned(Lo()), 1), "V"),
            "ConvertSignedACCurrentValueToString"
                or "ConvertSignedACCurrentValueToStringUsingRnDSignConvention"
                or "ConvertUnsignedACCurrentValueToString"
                => (Round(s.AcAmps(Lo()), 2), "A"),
            "ConvertSignedACFrequencyValueInCentiHzToString" => (Round(SpProScaling.Hertz(Lo()), 2), "Hz"),

            // --- DC -------------------------------------------------------------------------
            "ConvertSignedDCVoltageValueToString"
                or "ConvertSignedExpCardDCVoltageValueToString"
                or "ConvertUnsignedDCVoltageValueFormKacoToString"
                => (Round(s.DcVolts(Lo()), 2), "V"),
            "ConvertSignedDCCurrentValueToString"
                or "ConvertSignedDCShuntCurrentValueToString"
                or "ConvertUnsignedDCCurrentValueToString"
                => (Round(s.DcAmps(Lo()), 2), "A"),
            "Convert32bitSignedDCBatteryCurrentValueToString"
                or "Convert32bitSignedDCBatteryCurrentValueToDecimal"
                => (Round(s.DcAmps32(Lo(), Hi()), 2), "A"),
            "ConvertSignedDCShuntPowerValueToString"
                or "ConvertSignedDCPeakShuntPowerValueToString"
                => (Round(s.DcKilowatts16(Lo()), 3), "kW"),
            "Convert32bitSignedDCBatteryPowerValueToString"
                or "Convert32bitSignedDCBatteryPowerValueToStringSupressingNegValues"
                => (Round(s.DcKilowatts32(Lo(), Hi()), 3), "kW"),
            "ConvertUnSignedDCPowerValueToStringInWatts"
                or "ConvertUnSignedDCPowerValueToString"
                => (Round(Lo() * (s.DcVolts * (s.DcCurrent / 3276800.0)), 1), "W"),
            "ConvertSignedDCEnergyValueInKwhToString"
                or "ConvertSignedDCShuntEnergyValueInKwhToString"
                => (Round(s.DcKilowattHours(Lo()), 3), "kWh"),
            "Convert32bitUnSignedDCEnergyValueInKwhToString"
                or "Convert32bitSignedDCShuntEnergyValueInKwhToString"
                => (Round(s.DcKilowattHours32(Lo(), Hi()), 3), "kWh"),
            "Convert32bitSignedDCEnergyValueInKwhToString" => (Round(s.DcKilowattHoursSigned32(Lo(), Hi()), 3), "kWh"),
            "Convert32bitSignedDCEnergyValueInKwhToStringAndFlipSign"
                => (Round(-s.DcKilowattHoursSigned32(Lo(), Hi()), 3), "kWh"),
            "Convert32bitUnSignedDCBattEnergyValueInKwhToString" when r.Length >= 4
                => (Round(s.DcKilowattHours32(r[0], r[1]) - s.DcKilowattHours32(r[2], r[3]), 3), "kWh"),

            // --- time, temperature, percentages, plain numbers --------------------------------
            "ConvertTimeInMinutesValueToHoursString" => (Round(SpProScaling.Hours(Lo()), 2), "h"),
            "Convert32bitTimeInMinutesValueToHoursString" => (Round(SpProScaling.Hours32(Lo(), Hi()), 2), "h"),
            "ConvertSignedTemperatureValueToString"
                or "ConvertUnsignedTemperatureValueToString"
                or "ConvertSignedHottestTemperatureValueToString"
                or "ConvertSignedBattTemperatureValueToString"
                => (Round(s.Temperature(Lo()), 1), "°C"),
            "ConvertUnSignedSOCPercentageValueToString" => (SpProScaling.Percent(Lo()) is { } p ? Round(p, 2) : null, "%"),
            "ConvertUnSignedPercentageValueToString" => (Round(Lo() / 256.0, 2), "%"),
            "ConvertUnitlessDeciNumberValueToString" => (Round(SpProScaling.Deci(Lo()), 1), null),
            "ConvertUnitlessNumberValueToString" => ((object)Lo(), null),
            "ConvertSoftwareVersionNumberValueToString" => (Round(Lo() / 100.0, 2), null),
            "Convert32BitSerialNumberValueToString" => ((object)SpProScaling.Unsigned32(Lo(), Hi()), null),

            // --- enumerations already transcribed for the live decoder ------------------------
            "ConvertInverterModeValueToString" => (SpProLiveData.InverterModeName(Lo()), null),
            "ConvertAcSourceStatusValueToString" => (SpProLiveData.AcSourceStatusName(Lo()), null),
            "ConvertGeneratorStatusValueToString" => (SpProLiveData.GeneratorStatusName(Lo()), null),
            "ConvertGeneratorStartedBySlashRunningReasonValueToString" => (SpProLiveData.GeneratorReasonName(Lo()), null),
            "ConvertChargerStatusValueToString" => (SpProLiveData.ChargerStateName(Lo()), null),
            "ConvertChargerLockoutStatusValueToString" => (SpProLiveData.ChargerLockoutName(Lo()), null),

            // --- identity and configuration -------------------------------------------------
            "ConvertKacoModelNumberToString" => (SpProDisplayBlocks.KacoModelName(Lo()), null),
            "Convert32bitUnsignedValueInkWToString" => (Round(SpProScaling.Unsigned32(Lo(), Hi()) / 1000.0, 2), "kW"),
            "ConvertFactorySettingBooleanValueToString" => (Lo() != 0, null),
            "ConvertInverterModelValueToModelNumberString" when Lo() < (uint)SpProUnitInfo.ModelNumbers.Length
                => (SpProUnitInfo.ModelNumbers[Lo()] is { Length: > 0 } m ? m : null, null),

            // --- BCD date and time ------------------------------------------------------------
            // Stored two decimal digits to a byte, as the clock registers are.
            "ConvertDateThatWasStoredInBCDToString" when r.Length >= 2
                => (BcdDate(r), null),
            "ConvertTimeThatWasStoredInBCDToString" or "ConvertTimethatfloatwasachievedToString"
                => (BcdTime(Lo()), null),

            // --- small enumerations -----------------------------------------------------------
            "ConvertDigInput_OutputStatusValueToString" => (Lo() switch
            {
                0 => "Inactive",
                1 => "Active",
                _ => $"Unknown ({Lo()})",
            }, null),
            "ConvertDaysToEqualiseValueToString" => (Lo() == ushort.MaxValue ? "Disabled" : (object)Lo(), "d"),

            // --- bit flags and status words ---------------------------------------------------
            "BitStatus" when args.Length > 0 => (((1 << args[0]) & Lo()) != 0 ? "Active" : "Inactive", null),
            "ConvertModStatusNumberToString" => (ModStatus(Lo()), null),
            "ConvertStringInverterSupportValueToString" when r.Length >= 2
                => (StringInverterSupport(r[1]), null),
            "ConvertSoftwareVersionAndSringInverterSupportValueToString"
                => (Round(Lo() / 100.0, 2), null),

            // --- derived AC quantities ----------------------------------------------------------
            // Power factor needs real power as a 32-bit pair and reactive power as one word.
            "ConvertSignedACPowerFactorValueToString" when r.Length >= 3 => (PowerFactor(r, s), null),
            "ConvertSignedACSourceReactivePowerValueToString" => (Round(s.AcKilowatts16(Lo()), 3), "kvar"),

            // --- scaled scalars -----------------------------------------------------------------
            "ConvertChargeIndexValueToString" => (Round(Lo() / 2048.0, 3), null),
            "ConvertUnSigned14bitPercentageValueToString" => (Round(Lo() / 16384.0 * 100.0, 2), "%"),

            // --- identity -----------------------------------------------------------------------
            "ConvertCountryCodeValueToString" => (Lo() switch
            {
                0 or ushort.MaxValue => "Australia",
                1 => "Germany",
                2 => "USA",
                _ => $"Unknown ({Lo()})",
            }, null),
            "ConvertInverterModelValueToModelDescriptionString" when r.Length >= 1
                => (SpProUnitInfo.FromWords([Lo(), 0, 0, 25]).ModelDescription is { Length: > 0 } d ? d : null, null),
            "ConvertBMSModelValueToString" => (Lo() switch
            {
                0 => "None", 1 => "LG Chem", 2 => "BYD", 3 => "Pylontech",
                _ => $"Unknown ({Lo()})",
            }, null),
            "ConvertLGBmsModuleTypeToString" => (Lo() switch
            {
                0 => "None", 1 => "M4863P3S1", 2 => "M4863P3S2", 3 => "M4863P3S3",
                4 => "M4863P3S4", 5 => "M4863P3S5", 6 => "M4863P3S6", 7 => "M48126P3S7",
                _ => "Unknown",
            }, null),
            "ConvertLGVersionToString" => ($"{Lo() >> 8}.{Lo() & 0xFF}", null),
            "ConvertLG32bitBigEndianSerialNumberToString" when r.Length >= 2
                => (LgSerial(Lo(), Hi()), null),

            // --- BMS flag lists -------------------------------------------------------------------
            "ConvertLGBmsAlarmsValueToList" => (Flags(Lo(), LgAlarms), null),
            "ConvertLGBmsWarningsValueToList" => (Flags(Lo(), LgWarnings), null),
            "ConvertLGBmsModuleFaultsValuesToList" => (Flags(Lo(), LgModuleFaults), null),
            "ConvertLGBmsModuleAlarmsValueToList" => (Flags(Lo(), LgAlarms), null),
            "ConvertLGBmsModuleWarningsValueToList" => (Flags(Lo(), LgWarnings), null),

            // Raw means SP LINK formatted the register itself rather than calling a converter.
            "Raw" => ((object)Lo(), null),

            // Nothing left should reach here; if it does the raw words still travel with the field.
            _ => (null, null),
        };
    }

    private static object Round(double value, int digits) => Math.Round(value, digits);

    /// <summary>Lists the module positions whose bit is <em>clear</em>, which is how SP LINK reads it.</summary>
    private static string ModStatus(ushort value)
    {
        var present = Enumerable.Range(0, 16).Where(i => (value & (1 << i)) == 0).Select(i => i + 1);
        return string.Join(", ", present);
    }

    private static string StringInverterSupport(ushort sis)
    {
        if (sis == 65534) return "Dependent on Comms Card";
        var names = new List<string>();
        if ((sis & 1) != 0) names.Add("Powador");
        if ((sis & 2) != 0) names.Add("ABB / FIMER");
        if ((sis & 4) != 0) names.Add("Fronius");
        if ((sis & 8) != 0) names.Add("SelectSun");
        return names.Count > 0 ? string.Join(" / ", names) : "Unsupported";
    }

    /// <summary>
    /// Power factor from real power (a 32-bit pair) and reactive power, with the lead/lag sense
    /// SP LINK shows: the two agreeing in sign is leading.
    /// </summary>
    private static string PowerFactor(ushort[] r, SpProScaleFactors s)
    {
        var real = s.AcKilowatts32(r[0], r[1]);
        var reactive = s.AcKilowatts16(r[2]);
        var apparent = Math.Sqrt(real * real + reactive * reactive);
        var pf = apparent != 0 ? Math.Abs(real / apparent) : 1.0;
        var sense = (real > 0 && reactive > 0) || (real < 0 && reactive < 0) ? "Leading" : "Lagging";
        return Math.Round(pf, 3) == 1.0 ? "1.000" : $"{pf:0.000} {sense}";
    }

    /// <summary>LG serials are big-endian within each word, unlike everything else here.</summary>
    private static string? LgSerial(ushort lo, ushort hi)
    {
        static ushort Swap(ushort v) => (ushort)((v >> 8) | (v << 8));
        lo = Swap(lo); hi = Swap(hi);
        if (lo == ushort.MaxValue) return null;
        var value = (uint)lo * 65536u + hi;
        return value == 0 ? null : value.ToString();
    }

    private static readonly string[] LgAlarms =
    [
        "Sub Relay Command", "High Battery Voltage", "Low Battery Voltage", "High Battery Temperature",
        "Low Battery Temperature", "High Battery Charge Temperature", "Low Battery Charge Temperature",
        "High Discharge Current", "High Charge Current", "Communication Fault", "", "BMS internal Fault",
        "Imbalance in Cell Voltage", "", "Overload", "",
    ];

    private static readonly string[] LgWarnings =
    [
        "Sub Relay Command", "High Battery Voltage", "Low Battery Voltage", "High Battery Temperature",
        "Low Battery Temperature", "High Battery Charge Temperature", "Low Battery Charge Temperature",
        "High Discharge Current", "High Charge Current", "Communication Fault", "", "BMS internal Fault",
        "Imbalance in Cell Voltage", "", "Overload", "",
    ];

    private static readonly string[] LgModuleFaults =
    [
        "Cell Over Voltage", "Cell Under Voltage", "Over Current Charge", "Over Current Discharge",
        "Over Temperature", "Under Temperature", "Imbalance", "Internal Fault",
        "", "", "", "", "", "", "", "",
    ];

    /// <summary>Named bits that are set. Empty means nothing is flagged, which is not the same as unknown.</summary>
    private static string Flags(ushort value, string[] names)
    {
        var set = Enumerable.Range(0, 16)
            .Where(i => (value & (1 << i)) != 0 && names[i].Length > 0)
            .Select(i => names[i]);
        return string.Join(", ", set);
    }

    private static int Bcd(int b) => ((b >> 4) & 0xF) * 10 + (b & 0xF);

    /// <summary>Two words of packed BCD: day, month, year. Zero means "never set".</summary>
    private static string? BcdDate(ushort[] r)
    {
        if (r.All(x => x == 0)) return null;
        int day = Bcd(r[0] & 0xFF), month = Bcd(r[0] >> 8), year = Bcd(r[1] & 0xFF);
        if (day is < 1 or > 31 || month is < 1 or > 12) return null;
        return $"{2000 + year:D4}-{month:D2}-{day:D2}";
    }

    /// <summary>One word of packed BCD: hour, minute.</summary>
    private static string? BcdTime(ushort raw)
    {
        if (raw == 0 || raw == ushort.MaxValue) return null;
        int hour = Bcd(raw >> 8), minute = Bcd(raw & 0xFF);
        return hour < 24 && minute < 60 ? $"{hour:D2}:{minute:D2}" : null;
    }
}
