using System.Buffers.Binary;
using System.Text;

namespace SpLink.Protocol;

public enum FrameOp : byte
{
    Read = 0x51,  // 'Q'
    Write = 0x57, // 'W'
}

/// <summary>
/// Builds and validates SP PRO register frames.
/// <para>
/// Header (8 bytes): op, wordCount-1, 32-bit little-endian start address, CRC over those 6 bytes.
/// A write request appends wordCount little-endian 16-bit words and a CRC over the data bytes only.
/// A read response echoes the 8-byte header followed by the data words and their CRC; a write response echoes the request.
/// Because the CRC is KERMIT with zero init, a well-formed frame has zero residue over its full length.
/// </para>
/// </summary>
public static class SpProFrame
{
    public const int HeaderLength = 8;
    public const int CrcLength = 2;
    public const int MaxWords = 256;

    public static byte[] BuildRead(uint address, int wordCount)
    {
        ValidateWordCount(wordCount);
        var frame = new byte[HeaderLength];
        WriteHeader(frame, FrameOp.Read, address, wordCount);
        return frame;
    }

    public static byte[] BuildWrite(uint address, ReadOnlySpan<ushort> words)
    {
        ValidateWordCount(words.Length);
        var frame = new byte[HeaderLength + words.Length * 2 + CrcLength];
        WriteHeader(frame, FrameOp.Write, address, words.Length);
        var data = frame.AsSpan(HeaderLength, words.Length * 2);
        for (int i = 0; i < words.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(data.Slice(i * 2, 2), words[i]);
        Crc16Kermit.WriteCrc(data, frame.AsSpan(HeaderLength + data.Length, CrcLength));
        return frame;
    }

    private static void WriteHeader(Span<byte> frame, FrameOp op, uint address, int wordCount)
    {
        frame[0] = (byte)op;
        frame[1] = (byte)(wordCount - 1);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.Slice(2, 4), address);
        Crc16Kermit.WriteCrc(frame[..6], frame.Slice(6, CrcLength));
    }

    public static int ResponseLength(int wordCount) => HeaderLength + wordCount * 2 + CrcLength;

    /// <summary>Length of the response implied by a request/response header: header + data words + CRC.</summary>
    public static int ResponseLengthFromHeader(ReadOnlySpan<byte> header) => ResponseLength(header[1] + 1);

    public static bool IsStartByte(byte b) => b is (byte)FrameOp.Read or (byte)FrameOp.Write;
    public static FrameOp Op(ReadOnlySpan<byte> frame) => (FrameOp)frame[0];
    public static int WordCount(ReadOnlySpan<byte> frame) => frame[1] + 1;
    public static uint Address(ReadOnlySpan<byte> frame) => BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(2, 4));

    public static ushort[] DataWords(ReadOnlySpan<byte> frame)
    {
        int count = (frame.Length - HeaderLength - CrcLength) / 2;
        var words = new ushort[count];
        for (int i = 0; i < count; i++)
            words[i] = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(HeaderLength + i * 2, 2));
        return words;
    }

    /// <summary>Validates a response frame (or a write request, which has the same shape).</summary>
    public static bool TryValidate(ReadOnlySpan<byte> frame, out string? error)
    {
        if (frame.Length < HeaderLength + CrcLength) { error = $"frame too short ({frame.Length} bytes)"; return false; }
        if (!IsStartByte(frame[0])) { error = $"unexpected start byte 0x{frame[0]:X2}"; return false; }
        if (!Crc16Kermit.HasValidResidue(frame[..HeaderLength])) { error = "header CRC mismatch"; return false; }
        int expected = ResponseLengthFromHeader(frame);
        if (frame.Length != expected) { error = $"length {frame.Length} does not match header ({expected})"; return false; }
        if (!Crc16Kermit.HasValidResidue(frame)) { error = "data CRC mismatch"; return false; }
        error = null;
        return true;
    }

    /// <summary>Validates a request frame: an 8-byte read, or a write with data and CRC.</summary>
    public static bool TryValidateRequest(ReadOnlySpan<byte> frame, out string? error)
    {
        if (frame.Length < HeaderLength) { error = $"request too short ({frame.Length} bytes)"; return false; }
        if (!IsStartByte(frame[0])) { error = $"unexpected start byte 0x{frame[0]:X2}"; return false; }
        if (!Crc16Kermit.HasValidResidue(frame[..HeaderLength])) { error = "header CRC mismatch"; return false; }
        if (Op(frame) == FrameOp.Read)
        {
            if (frame.Length != HeaderLength) { error = $"read request should be {HeaderLength} bytes, got {frame.Length}"; return false; }
            error = null;
            return true;
        }
        return TryValidate(frame, out error);
    }

    public static string ToHex(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length * 3);
        foreach (var b in bytes) sb.Append(b.ToString("X2")).Append(' ');
        return sb.ToString().TrimEnd();
    }

    private static void ValidateWordCount(int wordCount)
    {
        if (wordCount is < 1 or > MaxWords)
            throw new ArgumentOutOfRangeException(nameof(wordCount), wordCount, $"word count must be 1..{MaxWords}");
    }
}
