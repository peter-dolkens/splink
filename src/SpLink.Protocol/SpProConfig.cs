using System.Globalization;

namespace SpLink.Protocol;

/// <summary>One configuration word, read back from the inverter.</summary>
public sealed record ConfigValue(ConfigBlock Block, int Index, string Name, string Converter, ushort Raw, string? Decoded)
{
    /// <summary>False when SP LINK applies a conversion we have not reproduced, so only <see cref="Raw"/> is trustworthy.</summary>
    public bool IsDecoded => Decoded is not null;
}

public sealed record SpProConfiguration(IReadOnlyList<ConfigValue> Settings, IReadOnlyDictionary<ConfigBlock, ushort[]> RawBlocks, SpProUnitInfo? Unit)
{
    public ConfigValue? this[string name] =>
        Settings.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Reads the SP PRO's configuration registers. Read-only: this never writes, and the library has no
/// configuration-write path at all, by design.
/// </summary>
public static class SpProConfigReader
{
    /// <summary>The five register reads SP LINK issues for a single inverter's configuration.</summary>
    public static readonly (ConfigBlock Block, uint Address, int Words, int Offset)[] Blocks =
    [
        (ConfigBlock.Common, 49152, 197, 0),
        (ConfigBlock.Common, 49381, 18, 197),   // merged onto the end of part 1
        (ConfigBlock.AppType, 49408, 125, 0),
        (ConfigBlock.BattType, 49536, 60, 0),
        (ConfigBlock.Scheduler, 51200, 193, 0),
    ];

    public static async Task<SpProConfiguration> ReadAsync(SpProClient client, CancellationToken cancellationToken = default)
    {
        // DC voltage settings scale by the battery cell count, which depends on the model, so identify the unit first.
        var unit = await client.ReadUnitInfoAsync(cancellationToken).ConfigureAwait(false);
        var raw = new Dictionary<ConfigBlock, ushort[]>();
        foreach (var (block, address, words, offset) in Blocks)
        {
            var read = await client.ReadWordsAsync(address, words, cancellationToken).ConfigureAwait(false);
            if (!raw.TryGetValue(block, out var existing))
            {
                raw[block] = offset == 0 ? read : Place(new ushort[offset + words], read, offset);
                continue;
            }
            var grown = new ushort[Math.Max(existing.Length, offset + words)];
            existing.CopyTo(grown, 0);
            raw[block] = Place(grown, read, offset);
        }

        var settings = new List<ConfigValue>(SpProConfigMap.All.Count);
        foreach (var s in SpProConfigMap.All)
        {
            if (!raw.TryGetValue(s.Block, out var words) || s.Index >= words.Length) continue;
            var value = words[s.Index];
            settings.Add(new ConfigValue(s.Block, s.Index, s.Name, s.Converter, value, Decode(s.Converter, value, unit.BatteryCellCount)));
        }
        return new SpProConfiguration(settings, raw, unit);
    }

    private static ushort[] Place(ushort[] target, ushort[] source, int offset)
    {
        source.CopyTo(target, offset);
        return target;
    }

    /// <summary>
    /// Renders the converters whose behaviour was confirmed in SP LINK's source. Anything else returns null
    /// so the caller sees the raw word rather than a plausible-looking guess.
    /// </summary>
    public static string? Decode(string converter, ushort raw, int batteryCellCount = 24)
    {
        // A table-driven converter is just a value->label lookup lifted straight from SP LINK.
        if (SpProConfigMap.EnumLabels.TryGetValue(converter, out var labels))
            return labels(raw) ?? $"Unknown ({raw})";

        return converter switch
        {
            "Logical" => raw switch { 0 => "Disabled", 1 => "Enabled", _ => $"Unknown ({raw})" },
            "Numerical" or "NumericalAndFitNud" => raw.ToString(CultureInfo.InvariantCulture),
            "Time" or "StopTimeWithoutDisable" => Minutes(raw),
            // 1440 minutes (24h) is SP LINK's "no stop time" sentinel.
            "StopTimeWithDisable" => raw < 1440 ? Minutes(raw) : "Disabled",
            "NumericalWhichIsStoredInDeciUnits" => Scaled(raw, 10),
            "NumericalWhichIsStoredInCentiUnits" => Scaled(raw, 100),
            "NumericalInUnitsWhichIsStoredInMiliUnits" => Scaled(raw, 1000),
            "NumericalWhichIsStoredInQuinquadeciUnits" => Scaled(raw, 15),
            "NumericalWhichIsStoredInDeciUnitsAndChangeSign" => Scaled((ushort)(-(short)raw), 10),
            "NumericalAndChangeSign" => (-(short)raw).ToString(CultureInfo.InvariantCulture),
            "NumericalWithOffSetOf10000" => (raw - 10000).ToString(CultureInfo.InvariantCulture),
            "NumericalWhichIsStoredInDeciUnitsOffset100" => ((decimal)raw / 10 - 100).ToString(CultureInfo.InvariantCulture),
            "NumericalWhichIsDisplayedInHoursButStoredInMinutes" => Scaled(raw, 60),
            "Numerical_TemperatureCoefficient" => Scaled(raw, 10),
            // Per-cell millivolts scaled up by the string's cell count.
            "DCVoltage" => $"{Math.Round(raw * batteryCellCount / 1000m, 1).ToString(CultureInfo.InvariantCulture)} V",
            // Stored in hundredths. The unit (kW/kVA/A) comes from a companion setting, so it is not asserted here.
            "InputPower" => Scaled(raw, 100),
            "BaudRate" => SpProRegisters.BaudCodeToString(raw),
            "CTRatio" => (raw * 5).ToString(CultureInfo.InvariantCulture),
            "Mode" => raw switch { 0 => "Idle", 1 => "Econo", 2 => "On", _ => $"Unknown ({raw})" },
            "ShuntName" => raw switch
            {
                0 => "None", 1 => "Solar", 2 => "Wind", 3 => "Hydro", 4 => "Charger", 5 => "Load",
                6 => "Dual", 7 => "Multiple SP PROs", 8 => "Log Only", 9 => "System SoC", 10 => "Direct SoC Input",
                _ => $"Unknown ({raw})",
            },
            // One half of a day/month pair; the pairing is a presentation concern, so report the number itself.
            "DayMonth" => raw.ToString(CultureInfo.InvariantCulture),
            // Low word of a 32-bit millivolt value; the high word lives in the next register.
            "AverageBatteryToStartGenerator" => Scaled(raw, 1000),
            _ => null,
        };
    }

    private static string Minutes(ushort raw) => $"{raw / 60:D2}:{raw % 60:D2}";

    private static string Scaled(ushort raw, int divisor) =>
        ((decimal)raw / divisor).ToString(CultureInfo.InvariantCulture);
}
