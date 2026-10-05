namespace SpLink.Protocol;

/// <summary>Identity of the connected inverter, read from <see cref="SpProRegisters.UnitInfo"/>.</summary>
public sealed record SpProUnitInfo(string Model, string ModelDescription, uint SerialNumber, int HardwareRevision, int BatteryCellCount)
{
    /// <summary>Model numbers indexed by the low byte of the model register. Slot 9 is unused.</summary>
    public static readonly string[] ModelNumbers =
        ["SPMC482", "SPMC481", "SPMC241", "SPLC1202", "SPMC1201", "SPMC240", "SPLC1201", "SPLC1200", "SPMC480", ""];

    /// <summary>
    /// The same model number means different hardware either side of revision 11, and the rating
    /// changed with it: an SPMC482 is 6 kW up to revision 10 and 7.5 kW from 11. SP LINK keeps two
    /// tables and picks on the revision word (mDataConvert.fnConvertInverterModelValueToModelDescriptionString).
    /// Every entry below matches the published ratings for its series.
    /// </summary>
    private static readonly string[] DescriptionsRev1To10 =
    [
        "48V DC, 6kW, 240V AC", "48V DC, 4kW, 240V AC", "24V DC, 3.5kW, 240V AC", "120V DC, 20kW, 240V AC",
        "120V DC, 6kW, 240V AC", "24V DC, 2.5kW, 240V AC", "120V DC, 15kW, 240V AC", "120V DC, 15kW, 240V AC",
        "48V DC, 2.5kW, 240V AC", "",
    ];

    private static readonly string[] DescriptionsRev11AndHigher =
    [
        "48V DC, 7.5kW, 240V AC", "48V DC, 5kW, 240V AC", "24V DC, 4.5kW, 240V AC", "120V DC, 20kW, 240V AC",
        "120V DC, 7.5kW, 240V AC", "24V DC, 3kW, 240V AC", "120V DC, 18kW, 240V AC", "120V DC, 15kW, 240V AC",
        "48V DC, 3.5kW, 240V AC", "",
    ];

    /// <summary>The revision at which the ratings step up.</summary>
    private const int FaceliftRevision = 11;

    /// <summary>
    /// Cells in the battery string as the inverter counts them, which sets the scale of every DC voltage
    /// setting. Indexed the same way as <see cref="ModelNumbers"/>.
    /// </summary>
    private static readonly int[] CellCounts = [24, 24, 12, 60, 60, 12, 60, 60, 24, 24];

    public static SpProUnitInfo FromWords(ReadOnlySpan<ushort> words)
    {
        if (words.Length < 4)
            throw new ArgumentException($"expected 4 unit-info words, got {words.Length}", nameof(words));

        int index = words[0] & 0xFF;
        int revision = words[3];
        bool known = index < ModelNumbers.Length && ModelNumbers[index].Length > 0;
        var descriptions = revision < FaceliftRevision ? DescriptionsRev1To10 : DescriptionsRev11AndHigher;

        return new SpProUnitInfo(
            known ? ModelNumbers[index] : $"Unknown ({index})",
            known ? descriptions[index] : "",
            (uint)(words[1] | (words[2] << 16)),
            revision,
            index < CellCounts.Length ? CellCounts[index] : 24);
    }
}
