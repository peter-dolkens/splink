namespace SpLink.Protocol;

/// <summary>
/// The arithmetic that turns raw register counts into engineering units.
/// <para>
/// Every analogue register is a count that only means something once multiplied by a model-specific
/// scale factor, and the divisors differ by width and quantity: 16-bit power divides by 3276800
/// where 32-bit power divides by 26214400, and energy carries an extra factor of 24. These are
/// transcribed from SP LINK's mDataConvert rather than inferred, because an almost-right divisor
/// produces a plausible number that is quietly wrong.
/// </para>
/// </summary>
public static class SpProScaling
{
    public static short Signed(ushort raw) => (short)raw;
    public static int Signed32(ushort lo, ushort hi) => (int)(lo | ((uint)hi << 16));
    public static uint Unsigned32(ushort lo, ushort hi) => lo | ((uint)hi << 16);

    // --- AC ---
    public static double AcVolts(this SpProScaleFactors s, ushort raw) => raw * (s.AcVolts / 327680.0);
    public static double AcVoltsSigned(this SpProScaleFactors s, ushort raw) => Signed(raw) * (s.AcVolts / 327680.0);
    public static double AcAmps(this SpProScaleFactors s, ushort raw) => Signed(raw) * (s.AcCurrent / 327680.0);

    /// <summary>16-bit AC power count to kW.</summary>
    public static double AcKilowatts16(this SpProScaleFactors s, ushort raw) =>
        Signed(raw) * (s.AcVolts * (s.AcCurrent / 3276800.0 / 1000.0));

    /// <summary>
    /// 16-bit AC power with the sign convention SP LINK shows on screen, which negates the raw
    /// value. Generator power and the regulation limits are stored the other way up, so without
    /// this a 9 kW charge limit reads as -9 kW.
    /// </summary>
    public static double AcKilowatts16Displayed(this SpProScaleFactors s, ushort raw) =>
        -s.AcKilowatts16(raw);

    /// <summary>16-bit AC power read as a magnitude, as SP LINK does for AC-coupled readings.</summary>
    public static double AcKilowatts16Unsigned(this SpProScaleFactors s, ushort raw) =>
        Math.Abs(raw * (s.AcVolts * (s.AcCurrent / 3276800.0 / 1000.0)));

    /// <summary>32-bit AC power count to kW. Note the divisor is 8x the 16-bit one.</summary>
    public static double AcKilowatts32(this SpProScaleFactors s, ushort lo, ushort hi) =>
        Signed32(lo, hi) * (s.AcVolts * (s.AcCurrent / 26214400.0 / 1000.0));

    /// <summary>16-bit AC energy accumulator to kWh.</summary>
    public static double AcKilowattHours(this SpProScaleFactors s, ushort raw) =>
        raw * (24.0 * (s.AcVolts * (s.AcCurrent / 3276800.0 / 1000.0)));

    public static double AcKilowattHours32(this SpProScaleFactors s, ushort lo, ushort hi) =>
        Unsigned32(lo, hi) * (24.0 * (s.AcVolts * (s.AcCurrent / 3276800.0 / 1000.0)));

    // --- DC ---
    public static double DcVolts(this SpProScaleFactors s, ushort raw) => Signed(raw) * (s.DcVolts / 327680.0);
    public static double DcAmps(this SpProScaleFactors s, ushort raw) => Signed(raw) * (s.DcCurrent / 327680.0);
    public static double DcAmps32(this SpProScaleFactors s, ushort lo, ushort hi) =>
        Signed32(lo, hi) * (s.DcCurrent / 327680.0);

    public static double DcKilowatts16(this SpProScaleFactors s, ushort raw) =>
        Signed(raw) * (s.DcVolts * (s.DcCurrent / 3276800.0 / 1000.0));

    public static double DcKilowatts32(this SpProScaleFactors s, ushort lo, ushort hi) =>
        Signed32(lo, hi) * (s.DcVolts * (s.DcCurrent / 3276800.0) / 1000.0);

    public static double DcKilowattHours(this SpProScaleFactors s, ushort raw) =>
        Signed(raw) * (24.0 * (s.DcVolts * (s.DcCurrent / 3276800.0 / 1000.0)));

    public static double DcKilowattHours32(this SpProScaleFactors s, ushort lo, ushort hi) =>
        Unsigned32(lo, hi) * (24.0 * (s.DcVolts * (s.DcCurrent / 3276800.0 / 1000.0)));

    /// <summary>
    /// 32-bit DC energy read as signed. The net accumulator genuinely goes negative, and reading it
    /// unsigned turns a small negative into a number in the hundreds of millions.
    /// </summary>
    public static double DcKilowattHoursSigned32(this SpProScaleFactors s, ushort lo, ushort hi) =>
        Signed32(lo, hi) * (24.0 * (s.DcVolts * (s.DcCurrent / 3276800.0 / 1000.0)));

    // --- everything else ---
    public static double Temperature(this SpProScaleFactors s, ushort raw) => Signed(raw) * (s.Temperature / 32768.0);

    /// <summary>Frequency is stored in centi-hertz.</summary>
    public static double Hertz(ushort raw) => Signed(raw) / 100.0;

    /// <summary>Percentages are stored as percent x 256. 0xFFFF means the feature is disabled.</summary>
    public static double? Percent(ushort raw) => raw == ushort.MaxValue ? null : raw / 256.0;

    public static double Hours(ushort minutes) => minutes / 60.0;
    public static double Hours32(ushort lo, ushort hi) => Unsigned32(lo, hi) / 60.0;
    public static double Deci(ushort raw) => raw / 10.0;

    /// <summary>
    /// Rounds the way SP LINK does: half away from zero, not .NET's default banker's rounding.
    /// <para>
    /// This is not a cosmetic difference here. The scale factors divide by powers of two
    /// (327680, 3276800), so raw counts land exactly on a midpoint far more often than in
    /// ordinary data — 15.625, 0.0625, 2.5 — and banker's rounding sends half of those the other
    /// way. SP LINK adds half and truncates (mLowLevelDataManipulation.RealRound), so matching it
    /// is what makes our output comparable with its CSV exports.
    /// </para>
    /// </summary>
    public static double Round(double value, int digits) =>
        Math.Round(value, digits, MidpointRounding.AwayFromZero);

    public static double? Round(double? value, int digits) =>
        value is null ? null : Round(value.Value, digits);
}
