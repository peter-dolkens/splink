namespace SpLink.Protocol;

/// <summary>
/// The running totals behind SP LINK's "Today" tab: energy in and out since midnight, plus the
/// lifetime accumulators that sit alongside them. These move slowly, so they are read on a much
/// longer cadence than live data.
/// </summary>
public sealed record SpProTodayReading
{
    public double DcInputKilowattHours { get; init; }
    public double DcOutputKilowattHours { get; init; }
    public double DcNetKilowattHours { get; init; }
    public double InverterDcKilowattHours { get; init; }
    public double BatteryInKilowattHours { get; init; }
    public double BatteryOutKilowattHours { get; init; }
    public double Shunt1KilowattHours { get; init; }
    public double Shunt2KilowattHours { get; init; }
    public double Shunt1PeakKilowatts { get; init; }
    public double Shunt2PeakKilowatts { get; init; }
    public double FloatHours { get; init; }
    public double AcLoadKilowattHours { get; init; }
    public double AcInputKilowattHours { get; init; }
    public double AcInputHours { get; init; }
    public double InverterRunHours { get; init; }
    public double AcExportKilowattHours { get; init; }

    /// <summary>
    /// Total AC-coupled solar energy today, and the same split for the first two inverters.
    /// Slots 3-5 are deliberately absent: see <see cref="SpProTodayData"/>.
    /// </summary>
    public double AcCoupledKilowattHours { get; init; }
    public double[] AcCoupledKilowattHoursPerInverter { get; init; } = [];

    /// <summary>The highest AC-coupled power seen today.</summary>
    public double AcCoupledPeakKilowatts { get; init; }

    public ushort[] RawWords { get; init; } = [];
}

/// <summary>
/// Decodes the "Today" block. Indices and converters come from SP LINK's subDisplayData_TodaySubTab.
/// Several fields moved when the memory map reached version 25, so the caller passes the version it
/// read from the inverter rather than this guessing.
/// </summary>
public static class SpProTodayData
{
    private const int DcInputLo = 0;
    private const int DcOutputLo = 2;
    private const int DcNetLo = 4;
    private const int BatteryInLo = 6;
    private const int BatteryOutLo = 8;
    private const int InverterDc = 10;
    private const int Shunt1Energy = 11;
    private const int Shunt2Energy = 12;
    private const int FloatMinutes = 13;
    private const int AcLoadLegacy = 15;
    private const int AcInputLegacy = 16;
    private const int AcInputMinutes = 17;
    private const int InverterRunMinutes = 18;
    private const int AcExportLegacy = 19;
    private const int Shunt1Peak = 20;
    private const int Shunt2Peak = 21;
    private const int AcCoupledTotalLegacy = 22;
    private const int AcCoupledPeak = 23;
    // Slots 1 and 2 only. SP LINK reads words 26-29 for AC-coupled inverters 3-5 *and* for the
    // battery-net accumulator, so those words are contested in its own source and are left alone
    // rather than published as a number that might be either.
    private const int AcCoupledPerInverter = 24;
    private const int AcCoupledInverterSlots = 2;
    private const int AcLoadV25 = 61;
    private const int AcExportV25 = 62;
    private const int AcInputV25 = 65;
    private const int AcCoupledTotalV25 = 67;

    /// <summary>The memory-map version at which the AC accumulators moved.</summary>
    private const int RelocatedAtMemoryMap = 25;

    public static SpProTodayReading Decode(ReadOnlySpan<ushort> w, SpProScaleFactors scale, int memoryMapVersion)
    {
        if (w.Length < SpProRegisters.TodayBlockWordCount)
            throw new ArgumentException($"expected {SpProRegisters.TodayBlockWordCount} words, got {w.Length}", nameof(w));

        var modern = memoryMapVersion >= RelocatedAtMemoryMap;
        var perInverter = new double[AcCoupledInverterSlots];
        for (var i = 0; i < perInverter.Length; i++)
            perInverter[i] = scale.AcKilowattHours(w[AcCoupledPerInverter + i]);

        return new SpProTodayReading
        {
            DcInputKilowattHours = scale.DcKilowattHours32(w[DcInputLo], w[DcInputLo + 1]),
            DcOutputKilowattHours = scale.DcKilowattHours32(w[DcOutputLo], w[DcOutputLo + 1]),
            // Signed, then flipped, exactly as SP LINK does: the accumulator goes negative, and
            // reading it unsigned turns -10 counts into half a billion kWh.
            DcNetKilowattHours = -scale.DcKilowattHoursSigned32(w[DcNetLo], w[DcNetLo + 1]),
            BatteryInKilowattHours = scale.DcKilowattHours32(w[BatteryInLo], w[BatteryInLo + 1]),
            BatteryOutKilowattHours = scale.DcKilowattHours32(w[BatteryOutLo], w[BatteryOutLo + 1]),
            InverterDcKilowattHours = scale.DcKilowattHours(w[InverterDc]),
            Shunt1KilowattHours = scale.DcKilowattHours(w[Shunt1Energy]),
            Shunt2KilowattHours = scale.DcKilowattHours(w[Shunt2Energy]),
            Shunt1PeakKilowatts = scale.DcKilowatts16(w[Shunt1Peak]),
            Shunt2PeakKilowatts = scale.DcKilowatts16(w[Shunt2Peak]),
            FloatHours = SpProScaling.Hours(w[FloatMinutes]),
            AcInputHours = SpProScaling.Hours(w[AcInputMinutes]),
            InverterRunHours = SpProScaling.Hours(w[InverterRunMinutes]),

            AcLoadKilowattHours = scale.AcKilowattHours(w[modern ? AcLoadV25 : AcLoadLegacy]),
            AcInputKilowattHours = scale.AcKilowattHours(w[modern ? AcInputV25 : AcInputLegacy]),
            AcExportKilowattHours = scale.AcKilowattHours(w[modern ? AcExportV25 : AcExportLegacy]),
            AcCoupledKilowattHours = scale.AcKilowattHours(w[modern ? AcCoupledTotalV25 : AcCoupledTotalLegacy]),
            AcCoupledKilowattHoursPerInverter = perInverter,
            AcCoupledPeakKilowatts = scale.AcKilowatts16Unsigned(w[AcCoupledPeak]),

            RawWords = w.ToArray(),
        };
    }
}
