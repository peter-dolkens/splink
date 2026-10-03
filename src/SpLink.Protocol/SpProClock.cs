namespace SpLink.Protocol;

/// <summary>Encodes and decodes the SP PRO real-time clock registers (packed BCD, one field per 16-bit word).</summary>
public static class SpProClock
{
    public const int ReadWordCount = 8;
    public const int WriteWordCount = 7;

    /// <summary>
    /// Decodes the 8 words read from <see cref="SpProRegisters.ClockRead"/>.
    /// The inverter stores only two year digits plus a century bit, so the millennium comes from <paramref name="referenceYear"/>.
    /// </summary>
    public static DateTime Decode(ReadOnlySpan<ushort> words, int referenceYear)
    {
        if (words.Length < ReadWordCount)
            throw new ArgumentException($"expected {ReadWordCount} clock words, got {words.Length}", nameof(words));

        byte ms = (byte)words[0], sec = (byte)words[1], min = (byte)words[2], hour = (byte)words[3];
        byte day = (byte)words[5], month = (byte)words[6], year = (byte)words[7];

        int millisecond = (ms >> 4) * 100 + (ms & 0xF) * 10;
        int monthValue = ((month >> 4) & 0x7) * 10 + (month & 0xF);
        int yearValue = referenceYear / 1000 * 1000 + (month >> 7) * 100 + FromBcd(year);
        string raw = string.Join(" ", words.ToArray().Select(w => w.ToString("X4")));

        try
        {
            return new DateTime(yearValue, monthValue, FromBcd(day), FromBcd(hour), FromBcd(min), FromBcd(sec), millisecond, DateTimeKind.Unspecified);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new SpProClockException($"SP PRO clock registers are out of range ({raw}); set the clock to recover", ex);
        }
    }

    public static DayOfWeek DecodeDayOfWeek(ReadOnlySpan<ushort> words) => (DayOfWeek)(words[4] & 0x7);

    /// <summary>Encodes a local date/time as the 7 words written to <see cref="SpProRegisters.ClockWrite"/>.</summary>
    public static ushort[] Encode(DateTime value)
    {
        int century = value.Year % 1000 / 100;
        if (value.Year < 2000 || century > 1)
            throw new ArgumentOutOfRangeException(nameof(value), value, "the SP PRO clock supports years 2000-2199");

        return
        [
            ToBcd(value.Second),
            ToBcd(value.Minute),
            ToBcd(value.Hour),
            (ushort)(int)value.DayOfWeek,
            ToBcd(value.Day),
            (ushort)((century << 7) | ToBcd(value.Month)),
            ToBcd(value.Year % 100),
        ];
    }

    private static ushort ToBcd(int value) => (ushort)(((value / 10) << 4) | (value % 10));
    private static int FromBcd(byte value) => (value >> 4) * 10 + (value & 0xF);
}
