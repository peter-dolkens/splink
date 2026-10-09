using SpLink.Protocol;

namespace SpLink.Protocol.Tests;

public class SpProServiceSettingsTests
{
    [Fact]
    public void MapNamesThirtyNineSettings()
    {
        // The contributor matched 39/39 against a SP LINK export; a transcription slip here would
        // silently mislabel a grid-protection parameter.
        Assert.Equal(39, SpProServiceSettings.All.Count);
    }

    [Fact]
    public void OffsetsAreUniqueAscendingAndInsideTheBlock()
    {
        var offsets = SpProServiceSettings.All.Select(s => s.Offset).ToList();
        Assert.Equal(offsets.OrderBy(o => o).ToList(), offsets);
        Assert.Equal(offsets.Distinct().Count(), offsets.Count);
        Assert.All(offsets, o => Assert.InRange(o, 0, SpProServiceSettings.WordCount - 1));
    }

    [Fact]
    public void AddressesResolveFromTheBlockBase()
    {
        Assert.Equal(49665u, SpProServiceSettings.All.First(s => s.Name == "FanType").Address);
        // AllowPowerOverride gates Modbus 8032/8033, so its address is worth pinning explicitly.
        Assert.Equal(49678u, SpProServiceSettings.All.First(s => s.Name == "AllowPowerOverride").Address);
        Assert.Equal(49706u, SpProServiceSettings.All.First(s => s.Name == "DefaultExportLimitRampRate").Address);
    }

    [Fact]
    public void SpacerOffsetsAreLeftUnnamed()
    {
        // 6-8 are spacers reading 0000/FFFF/0000. Naming them would invent settings that do not exist.
        var named = SpProServiceSettings.All.Select(s => s.Offset).ToHashSet();
        Assert.DoesNotContain(6, named);
        Assert.DoesNotContain(7, named);
        Assert.DoesNotContain(8, named);
    }
}
