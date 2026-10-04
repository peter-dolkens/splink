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
