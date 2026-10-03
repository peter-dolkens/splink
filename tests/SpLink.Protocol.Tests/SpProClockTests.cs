using SpLink.Protocol;
using Xunit;

namespace SpLink.Protocol.Tests;

public class SpProClockTests
{
    [Fact]
    public void Encodes_as_bcd_in_sp_link_field_order()
    {
        var saturday = new DateTime(2026, 10, 3, 14, 5, 9);
        Assert.Equal(DayOfWeek.Saturday, saturday.DayOfWeek);

        ushort[] expected = [0x09, 0x05, 0x14, 6, 0x03, 0x10, 0x26];
        Assert.Equal(expected, SpProClock.Encode(saturday));
    }

    [Fact]
    public void Sets_century_bit_for_2100s()
    {
        var words = SpProClock.Encode(new DateTime(2126, 12, 31, 23, 59, 59));
        Assert.Equal(0x80 | 0x12, words[5]);
        Assert.Equal(0x26, words[6]);
    }

    [Fact]
    public void Decodes_eight_words_including_milliseconds()
    {
        ushort[] words = [0x12, 0x09, 0x05, 0x14, 6, 0x03, 0x10, 0x26];
        var time = SpProClock.Decode(words, referenceYear: 2026);

        Assert.Equal(new DateTime(2026, 10, 3, 14, 5, 9, 120), time);
        Assert.Equal(DayOfWeek.Saturday, SpProClock.DecodeDayOfWeek(words));
    }

    [Fact]
    public void Round_trips_through_encode_and_decode()
    {
        var original = new DateTime(2031, 2, 28, 0, 0, 0);
        var words = SpProClock.Encode(original);
        var decoded = SpProClock.Decode([0, .. words], referenceYear: 2031);
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Rejects_years_outside_the_inverters_range()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SpProClock.Encode(new DateTime(1999, 12, 31)));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpProClock.Encode(new DateTime(2200, 1, 1)));
    }

    [Fact]
    public void Reports_garbage_registers_as_a_clock_error()
    {
        ushort[] garbage = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
        var ex = Assert.Throws<SpProClockException>(() => SpProClock.Decode(garbage, 2026));
        Assert.Contains("out of range", ex.Message);
    }
}
