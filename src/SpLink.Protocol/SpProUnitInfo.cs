namespace SpLink.Protocol;

/// <summary>Identity of the connected inverter, read from <see cref="SpProRegisters.UnitInfo"/>.</summary>
public sealed record SpProUnitInfo(string Model, string ModelDescription, uint SerialNumber, int HardwareRevision, int BatteryCellCount)
{
    /// <summary>Model numbers indexed by the low byte of the model register.</summary>
    public static readonly string[] ModelNumbers =
        ["SPMC482", "SPMC481", "SPMC241", "SPLC1202", "SPMC1201", "SPMC240", "SPLC1201", "SPLC1200", "SPMC480", "SPMC485"];

    private static readonly string[] Descriptions =
    [
        "48V DC, 6kW, 240V AC", "48V DC, 4kW, 240V AC", "24V DC, 3.5kW, 240V AC", "120V DC, 20kW, 240V AC",
        "120V DC, 15kW, 240V AC", "24V DC, 3kW, 240V AC", "120V DC, 15kW, 240V AC", "120V DC, 12kW, 240V AC",
        "48V DC, 5kW, 240V AC", "48V DC, 7.5kW, 240V AC",
    ];

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
        bool known = index < ModelNumbers.Length;
        return new SpProUnitInfo(
            known ? ModelNumbers[index] : $"Unknown ({index})",
            known ? Descriptions[index] : "",
            (uint)(words[1] | (words[2] << 16)),
            words[3],
            known ? CellCounts[index] : 24);
    }
}
