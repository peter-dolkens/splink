namespace SpLink.Protocol;

/// <summary>
/// CRC-16/KERMIT (reflected polynomial 0x1021 → 0x8408, init 0, no final XOR), appended little-endian.
/// This is the "FCS" used on every SP PRO frame; a frame with its CRC appended has a residue of zero.
/// </summary>
public static class Crc16Kermit
{
    private static readonly ushort[] Table = BuildTable();

    private static ushort[] BuildTable()
    {
        var table = new ushort[256];
        for (int i = 0; i < table.Length; i++)
        {
            ushort crc = (ushort)i;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0x8408) : (ushort)(crc >> 1);
            table[i] = crc;
        }
        return table;
    }

    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (var b in data)
            crc = (ushort)((crc >> 8) ^ Table[(crc ^ b) & 0xFF]);
        return crc;
    }

    /// <summary>True when <paramref name="frameWithCrc"/> ends with a CRC that matches the preceding bytes.</summary>
    public static bool HasValidResidue(ReadOnlySpan<byte> frameWithCrc) => Compute(frameWithCrc) == 0;

    public static void WriteCrc(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        var crc = Compute(data);
        destination[0] = (byte)crc;
        destination[1] = (byte)(crc >> 8);
    }
}
