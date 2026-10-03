using System.Text;
using SpLink.Protocol;
using Xunit;

namespace SpLink.Protocol.Tests;

public class Crc16KermitTests
{
    [Fact]
    public void Matches_the_standard_kermit_check_value()
    {
        Assert.Equal(0x2189, Crc16Kermit.Compute(Encoding.ASCII.GetBytes("123456789")));
    }

    [Theory]
    [InlineData(1, 0x1189)] // SP LINK's FCSLookUpTable[1] = 4489
    [InlineData(2, 0x2312)] // [2] = 8978
    [InlineData(3, 0x329B)] // [3] = 12955
    public void Single_byte_values_match_sp_links_lookup_table(byte input, int expected)
    {
        Assert.Equal(expected, Crc16Kermit.Compute([input]));
    }

    [Fact]
    public void Appending_the_crc_gives_zero_residue()
    {
        var data = new byte[] { 0x51, 0x00, 0x00, 0xA0, 0x00, 0x00 };
        var frame = new byte[data.Length + 2];
        data.CopyTo(frame, 0);
        Crc16Kermit.WriteCrc(data, frame.AsSpan(data.Length));

        Assert.True(Crc16Kermit.HasValidResidue(frame));
        frame[3] ^= 0x01;
        Assert.False(Crc16Kermit.HasValidResidue(frame));
    }
}
