using System.Security.Cryptography;
using System.Text;

namespace SpLink.Protocol;

/// <summary>The SP PRO challenge/response login, reproduced from SP LINK's ConnectToInverter.</summary>
public static class SpProLogin
{
    public const string DefaultPassword = "Selectronic SP PRO";
    public const int ChallengeLength = 16;
    public const int MaxPasswordLength = 32;

    /// <summary>
    /// MD5 over (16-byte challenge ‖ password space-padded to 32 ASCII bytes), packed as 8 words.
    /// SP LINK packs the digest with its "LittleEndian" helper, which actually puts the even digest byte in the
    /// high half of each word — so on the wire the digest appears with each byte pair swapped. Reproduced exactly.
    /// </summary>
    public static ushort[] ComputeResponseWords(ReadOnlySpan<byte> challenge, string password)
    {
        if (challenge.Length != ChallengeLength)
            throw new ArgumentException($"challenge must be {ChallengeLength} bytes", nameof(challenge));
        if (password.Length > MaxPasswordLength)
            throw new ArgumentException($"password cannot exceed {MaxPasswordLength} characters", nameof(password));

        var material = new byte[ChallengeLength + MaxPasswordLength];
        challenge.CopyTo(material);
        Encoding.ASCII.GetBytes(password.PadRight(MaxPasswordLength, ' '), material.AsSpan(ChallengeLength));

        var digest = MD5.HashData(material);
        var words = new ushort[digest.Length / 2];
        for (int i = 0; i < words.Length; i++)
            words[i] = (ushort)(digest[2 * i + 1] | (digest[2 * i] << 8));
        return words;
    }
}
