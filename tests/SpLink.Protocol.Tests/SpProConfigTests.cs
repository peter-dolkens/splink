using SpLink.Protocol;
using SpLink.Protocol.Simulation;
using Xunit;

namespace SpLink.Protocol.Tests;

public class SpProConfigTests
{
    [Fact]
    public void Every_recovered_setting_decodes()
    {
        var undecodable = SpProConfigMap.All
            .Where(s => SpProConfigReader.Decode(s.Converter, 0) is null)
            .Select(s => s.Converter)
            .Distinct()
            .ToList();

        Assert.Empty(undecodable);
    }

    [Fact]
    public void Map_is_complete_and_has_no_duplicate_slots()
    {
        Assert.Equal(563, SpProConfigMap.All.Count);
        var duplicates = SpProConfigMap.All.GroupBy(s => (s.Block, s.Index)).Where(g => g.Count() > 1).ToList();
        Assert.Empty(duplicates);
        Assert.All(SpProConfigMap.All, s => Assert.False(string.IsNullOrWhiteSpace(s.Name)));
    }

    [Theory]
    [InlineData("Logical", 0, "Disabled")]
    [InlineData("Logical", 1, "Enabled")]
    [InlineData("Time", 1320, "22:00")]
    [InlineData("Time", 0, "00:00")]
    [InlineData("StopTimeWithDisable", 1440, "Disabled")]
    [InlineData("StopTimeWithDisable", 90, "01:30")]
    [InlineData("Numerical", 1012, "1012")]
    [InlineData("NumericalWhichIsStoredInDeciUnits", 5, "0.5")]
    [InlineData("InputPower", 500, "5")]
    [InlineData("CTRatio", 20, "100")]
    [InlineData("BaudRate", 7, "115200")]
    [InlineData("Mode", 1, "Econo")]
    [InlineData("ShuntName", 1, "Solar")]
    public void Converters_match_sp_links_rendering(string converter, ushort raw, string expected)
    {
        Assert.Equal(expected, SpProConfigReader.Decode(converter, raw));
    }

    [Theory]
    [InlineData("BatteryType", 10, "Lithium PowerPlus")]
    [InlineData("BatteryType", 2, "Lithium LiFePO4")]
    [InlineData("AppType", 0, "Off Grid")]
    [InlineData("EdgeSelection", 1, "Falling")]
    [InlineData("ChargerLockout", 1, "Renewable Only")]
    public void Enum_tables_come_from_sp_link(string converter, ushort raw, string expected)
    {
        Assert.Equal(expected, SpProConfigReader.Decode(converter, raw));
    }

    [Fact]
    public void Dc_voltages_scale_by_the_models_cell_count()
    {
        // SPMC482 is a 24-cell 48 V unit; 2321 mV/cell is the float voltage read from the real inverter.
        Assert.Equal("55.7 V", SpProConfigReader.Decode("DCVoltage", 2321, batteryCellCount: 24));
        Assert.Equal("27.9 V", SpProConfigReader.Decode("DCVoltage", 2321, batteryCellCount: 12));
    }

    [Fact]
    public void Unknown_enum_values_are_reported_rather_than_hidden()
    {
        Assert.Equal("Unknown (999)", SpProConfigReader.Decode("BatteryType", 999));
    }

    [Fact]
    public void Unit_info_decodes_model_serial_and_cell_count()
    {
        // A made-up serial: the 32-bit split is what is under test, and keeping a real one here
        // only invites it into the public repo, where it has to be scrubbed out again every sync.
        var unit = SpProUnitInfo.FromWords([0, 0x86A1, 0x0001, 25]);

        Assert.Equal("SPMC482", unit.Model);
        Assert.Equal(100001u, unit.SerialNumber);
        Assert.Equal(25, unit.HardwareRevision);
        Assert.Equal(24, unit.BatteryCellCount);
    }

    [Fact]
    public async Task Reads_every_configuration_block_from_the_device()
    {
        var fake = new FakeSpPro(ticksInRealTime: false);
        fake.ConfigWords[49152 + 63] = 1012;  // Common[63] BatterySize
        fake.ConfigWords[49152 + 1] = 10;     // Common[1]  BatteryType
        fake.ConfigWords[49536 + 18] = 2321;  // BattType[18] FloatV

        await using var client = new SpProClient(fake);
        await client.ConnectAsync();
        var config = await client.ReadConfigurationAsync();

        Assert.Equal(563, config.Settings.Count);
        Assert.All(config.Settings, s => Assert.True(s.IsDecoded));
        Assert.Equal("1012", config["BatterySize"]!.Decoded);
        Assert.Equal("Lithium PowerPlus", config["BatteryType"]!.Decoded);
        Assert.Equal("55.7 V", config["FloatV"]!.Decoded);
        Assert.Equal("SPMC482", config.Unit!.Model);

        // The two Common reads must be stitched into one block, with part 2 landing at offset 197.
        Assert.Equal(215, config.RawBlocks[ConfigBlock.Common].Length);
    }

    [Fact]
    public async Task Reading_configuration_issues_no_writes()
    {
        var fake = new FakeSpPro(ticksInRealTime: false);
        await using var client = new SpProClient(fake);
        await client.ConnectAsync();
        var before = fake.RequestLog.Count(r => SpProFrame.Op(r) == FrameOp.Write);

        await client.ReadConfigurationAsync();

        Assert.Equal(before, fake.RequestLog.Count(r => SpProFrame.Op(r) == FrameOp.Write));
    }
}

public class ChangeSignConverterTests
{
    [Theory]
    // Real values read off the build-site SP PRO. Each pairs with a non-Connect twin that uses
    // a different converter and must agree: ACFreqMin is -4, ACVMin is -10.
    [InlineData(40, "-4")]
    [InlineData(100, "-10")]
    [InlineData(0, "0")]
    [InlineData(5, "-0.5")]
    public void Deci_change_sign_negates_the_unsigned_word(ushort raw, string expected) =>
        Assert.Equal(expected, SpProConfigReader.Decode("NumericalWhichIsStoredInDeciUnitsAndChangeSign", raw));

    [Theory]
    [InlineData(4, "-4")]
    [InlineData(10, "-10")]
    [InlineData(0, "0")]
    public void Change_sign_negates_the_unsigned_word(ushort raw, string expected) =>
        Assert.Equal(expected, SpProConfigReader.Decode("NumericalAndChangeSign", raw));

    [Theory]
    [InlineData("NumericalWhichIsStoredInDeciUnitsAndChangeSign", (ushort)40000, "-4000")]
    [InlineData("NumericalAndChangeSign", (ushort)40000, "-40000")]
    public void A_word_above_short_max_is_still_a_magnitude_not_a_signed_value(
        string converter, ushort raw, string expected) =>
        // SP LINK computes -1 * value on the unsigned word, so a high word stays negative.
        // Reinterpreting it as a signed short would flip it positive — the bug this guards.
        Assert.Equal(expected, SpProConfigReader.Decode(converter, raw));

    [Fact]
    public void The_connect_window_matches_its_unscaled_twin()
    {
        // AppType 14/123 and 12/121 describe the same limits through different converters.
        // Agreement between them is what proves the decode, so assert it directly.
        Assert.Equal(SpProConfigReader.Decode("NumericalAndChangeSign", 4),
                     SpProConfigReader.Decode("NumericalWhichIsStoredInDeciUnitsAndChangeSign", 40));
        Assert.Equal(SpProConfigReader.Decode("NumericalAndChangeSign", 10),
                     SpProConfigReader.Decode("NumericalWhichIsStoredInDeciUnitsAndChangeSign", 100));
    }
}

public class UnitInfoDescriptionTests
{
    private static SpProUnitInfo Decode(int modelIndex, int revision) =>
        SpProUnitInfo.FromWords([(ushort)modelIndex, 0x86A1, 0x0001, (ushort)revision]);

    [Fact]
    public void Rating_steps_up_at_hardware_revision_11()
    {
        // The same model number is a different machine either side of the facelift.
        Assert.Equal("48V DC, 6kW, 240V AC", Decode(0, 10).ModelDescription);
        Assert.Equal("48V DC, 7.5kW, 240V AC", Decode(0, 11).ModelDescription);
    }

    [Theory]
    // Rev 11+ descriptions against the published SP PRO Series 2i ratings (BR0007_26),
    // which is independent of the source they were lifted from.
    [InlineData(0, "SPMC482", "48V DC, 7.5kW, 240V AC")]
    [InlineData(1, "SPMC481", "48V DC, 5kW, 240V AC")]
    [InlineData(2, "SPMC241", "24V DC, 4.5kW, 240V AC")]
    [InlineData(3, "SPLC1202", "120V DC, 20kW, 240V AC")]
    [InlineData(4, "SPMC1201", "120V DC, 7.5kW, 240V AC")]
    [InlineData(5, "SPMC240", "24V DC, 3kW, 240V AC")]
    [InlineData(7, "SPLC1200", "120V DC, 15kW, 240V AC")]
    [InlineData(8, "SPMC480", "48V DC, 3.5kW, 240V AC")]
    public void Current_hardware_matches_the_published_datasheet(int index, string model, string description)
    {
        var unit = Decode(index, 25);
        Assert.Equal(model, unit.Model);
        Assert.Equal(description, unit.ModelDescription);
    }

    [Fact]
    public void The_unused_model_slot_is_not_invented()
    {
        // Slot 9 is empty upstream. Naming it invites a wrong rating on hardware we have never seen.
        var unit = Decode(9, 25);
        Assert.Equal("Unknown (9)", unit.Model);
        Assert.Equal("", unit.ModelDescription);
    }

    [Fact]
    public void A_revision_25_spmc482_reads_as_its_datasheet_says()
    {
        // Model word 0 with revision 25, as read off real hardware.
        var unit = SpProUnitInfo.FromWords([0x0000, 0x86A1, 0x0001, 0x0019]);
        Assert.Equal("SPMC482", unit.Model);
        Assert.Equal("48V DC, 7.5kW, 240V AC", unit.ModelDescription);
        Assert.Equal(25, unit.HardwareRevision);
        Assert.Equal(24, unit.BatteryCellCount);
    }
}

public class ScalingTests
{
    // The build-site SP PRO's own scale factors: acV, acA, dcV, dcA, temp, internal.
    private static readonly SpProScaleFactors Scale = SpProScaleFactors.FromWords([5300, 2934, 1050, 16000, 530, 180]);

    [Fact]
    public void Ac_coupled_power_matches_what_the_solar_inverters_report()
    {
        // Word 84 read 20 while the two Fronius each reported ~47 W over their own API.
        Assert.Equal(0.0949, Scale.AcKilowatts16Unsigned(20), 4);
        Assert.Equal(0.0475, Scale.AcKilowatts16Unsigned(10), 4);
    }

    [Fact]
    public void Ac_energy_carries_the_extra_factor_of_24()
    {
        // 16 counts in the Today block is 1.822 kWh, which SP LINK agrees with.
        Assert.Equal(1.822, Scale.AcKilowattHours(16), 3);
    }

    [Fact]
    public void Sixteen_and_thirty_two_bit_ac_power_use_different_divisors()
    {
        // The 32-bit divisor is 8x the 16-bit one; using one for the other is a silent 8x error.
        Assert.Equal(8.0, Scale.AcKilowatts16(1000) / Scale.AcKilowatts32(1000, 0), 6);
    }

    [Fact]
    public void Net_dc_energy_is_signed()
    {
        // -10 counts. Read unsigned this lands near half a billion kWh, which is how the bug showed.
        var net = Scale.DcKilowattHoursSigned32(65526, 65535);
        Assert.Equal(-1.230, net, 3);
        Assert.True(Math.Abs(net) < 100, "a signed read must not overflow into millions");
    }

    [Fact]
    public void Displayed_ac_power_flips_the_stored_sign()
    {
        // A stored -1896 is a +9 kW charge limit, not a negative one.
        Assert.Equal(8.998, Scale.AcKilowatts16Displayed(63640), 3);
    }

    [Theory]
    [InlineData(25600, 100.0)]
    [InlineData(12800, 50.0)]
    public void Percentages_are_stored_times_256(ushort raw, double expected) =>
        Assert.Equal(expected, SpProScaling.Percent(raw)!.Value, 6);

    [Fact]
    public void A_disabled_percentage_reads_as_null() =>
        Assert.Null(SpProScaling.Percent(0xFFFF));

    [Fact]
    public void Frequency_is_centi_hertz() => Assert.Equal(50.0, SpProScaling.Hertz(5000), 6);
}

public class RoundingTests
{
    [Theory]
    // Reported against a second SPMC482-AU: SP LINK rounds half away from zero, where .NET's
    // Math.Round defaults to banker's. 15.625 -> 15.63 is the reporter's own example.
    [InlineData(15.625, 2, 15.63)]
    [InlineData(-15.625, 2, -15.63)]
    [InlineData(0.125, 2, 0.13)]
    [InlineData(0.0625, 3, 0.063)]
    [InlineData(2.5, 0, 3.0)]
    [InlineData(3.5, 0, 4.0)]
    public void Rounds_half_away_from_zero_as_sp_link_does(double value, int digits, double expected) =>
        Assert.Equal(expected, SpProScaling.Round(value, digits));

    [Fact]
    public void Real_readings_land_on_midpoints_at_a_regular_stride()
    {
        // Why the rounding mode is not cosmetic: the scale factors are dyadic, so exact midpoints
        // recur at a fixed interval in the raw counts rather than turning up by chance. Battery
        // current at two decimals hits one every 128 counts, which ordinary readings pass through
        // constantly. Banker's rounding would send half of them the other way.
        var scale = SpProScaleFactors.FromWords([5300, 2934, 1050, 16000, 530, 180]);

        Assert.Equal(3.125, scale.DcAmps(64), 9);
        Assert.Equal(3.13, SpProScaling.Round(scale.DcAmps(64), 2));
        Assert.Equal(3.12, Math.Round(scale.DcAmps(64), 2));   // what .NET would have given

        var midpoints = Enumerable.Range(1, 1280)
            .Count(i => Math.Abs(scale.DcAmps((ushort)i) * 100 % 1 - 0.5) < 1e-9);
        Assert.Equal(10, midpoints);
    }

    [Fact]
    public void A_null_reading_stays_null() => Assert.Null(SpProScaling.Round(null, 2));
}

public class FastReadTests
{
    private static readonly SpProScaleFactors Scale =
        SpProScaleFactors.FromWords([5300, 2934, 1050, 16000, 530, 180]);

    private static Dictionary<int, ushort> Words() => new()
    {
        [0] = 233, [1] = 0,          // inverter AC power, 32-bit
        [34] = 0, [35] = 0,          // AC load power, 32-bit
        [36] = 4,                    // charger: Return to Float
        [37] = 2,                    // inverter mode: On
        [40] = 0,                    // AC source: not present
        [41] = 25600,                // SoC 100%
        [43] = 65521,                // DC current, signed -15
        [44] = 14, [45] = 0,         // battery current, 32-bit
    };

    [Fact]
    public void Covers_every_word_the_fast_windows_read()
    {
        // The windows and the decoder have to agree, or a field silently throws at runtime.
        var covered = SpProRegisters.FastWindows
            .SelectMany(w => Enumerable.Range(w.Offset, w.Count)).ToHashSet();
        foreach (var needed in new[] { 0, 1, 34, 35, 36, 37, 40, 41, 43, 44, 45 })
            Assert.Contains(needed, covered);
    }

    [Fact]
    public void Reads_far_fewer_registers_than_the_whole_block()
    {
        var registers = SpProRegisters.FastWindows.Sum(w => w.Count);
        Assert.Equal(14, registers);
        Assert.True(registers * 6 < SpProRegisters.NowBlockWordCount,
                    "the fast path should be at least six times cheaper, or it is not worth having");
    }

    [Fact]
    public void Decodes_the_same_values_the_full_block_would()
    {
        var fast = SpProFastData.Decode(Words(), Scale);
        Assert.Equal(0.138, fast.InverterAcKilowatts, 3);
        Assert.Equal(0.0, fast.AcLoadKilowatts, 3);
        Assert.Equal("Return to Float", fast.ChargerState);
        Assert.Equal("On", fast.InverterMode);
        Assert.Equal("AC Source Not Present", fast.AcSourceStatus);
        Assert.Equal(100.0, fast.BatterySoCPercent!.Value, 3);
        Assert.Equal(0.68, fast.BatteryAmps, 2);
        Assert.Equal(-0.73, fast.DcAmps, 2);
    }

    [Fact]
    public void A_missing_word_is_an_error_rather_than_a_wrong_number()
    {
        var words = Words();
        words.Remove(34);
        Assert.Throws<SpProProtocolException>(() => SpProFastData.Decode(words, Scale));
    }
}
