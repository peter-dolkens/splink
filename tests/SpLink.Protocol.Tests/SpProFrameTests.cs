using SpLink.Protocol;
using Xunit;

namespace SpLink.Protocol.Tests;

public class SpProFrameTests
{
    [Fact]
    public void Read_request_has_sp_link_layout()
    {
        var frame = SpProFrame.BuildRead(SpProRegisters.LinkPort, 1);

        Assert.Equal(8, frame.Length);
        Assert.Equal(new byte[] { 0x51, 0x00, 0x00, 0xA0, 0x00, 0x00 }, frame[..6]);
        Assert.True(Crc16Kermit.HasValidResidue(frame));
        Assert.True(SpProFrame.TryValidateRequest(frame, out var error), error);
        Assert.Equal(12, SpProFrame.ResponseLengthFromHeader(frame));
    }

    [Fact]
    public void Read_of_eight_words_expects_26_byte_response()
    {
        var frame = SpProFrame.BuildRead(SpProRegisters.LoginChallenge, 8);
        Assert.Equal(7, frame[1]);
        Assert.Equal(26, SpProFrame.ResponseLengthFromHeader(frame));
    }

    [Fact]
    public void Write_request_carries_little_endian_words_and_data_crc()
    {
        ushort[] words = [0x0009, 0x0005, 0x0014, 0x0006, 0x0003, 0x0010, 0x0026];
        var frame = SpProFrame.BuildWrite(SpProRegisters.ClockWrite, words);

        Assert.Equal(8 + 14 + 2, frame.Length);
        Assert.Equal(0x57, frame[0]);
        Assert.Equal(6, frame[1]);
        Assert.Equal(SpProRegisters.ClockWrite, SpProFrame.Address(frame));
        Assert.Equal(new byte[] { 0x09, 0x00, 0x05, 0x00 }, frame[8..12]);
        Assert.True(Crc16Kermit.HasValidResidue(frame[..8]));
        Assert.True(Crc16Kermit.HasValidResidue(frame.AsSpan(8, 16)));
        Assert.True(SpProFrame.TryValidate(frame, out var error), error);
        Assert.Equal(words, SpProFrame.DataWords(frame));
    }

    [Fact]
    public void Validation_rejects_corruption()
    {
        var frame = SpProFrame.BuildWrite(1, [0x1234]);
        Assert.True(SpProFrame.TryValidate(frame, out _));

        var badData = (byte[])frame.Clone();
        badData[8] ^= 0xFF;
        Assert.False(SpProFrame.TryValidate(badData, out var error));
        Assert.Contains("data CRC", error);

        var badHeader = (byte[])frame.Clone();
        badHeader[2] ^= 0x01;
        Assert.False(SpProFrame.TryValidate(badHeader, out error));
        Assert.Contains("header CRC", error);

        var badStart = (byte[])frame.Clone();
        badStart[0] = 0x00;
        Assert.False(SpProFrame.TryValidate(badStart, out error));
        Assert.Contains("start byte", error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(257)]
    public void Word_count_is_bounded(int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SpProFrame.BuildRead(0, count));
    }
}
