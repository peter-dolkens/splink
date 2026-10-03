using System.Security.Cryptography;
using System.Text;
using SpLink.Protocol;
using Xunit;

namespace SpLink.Protocol.Tests;

public class SpProLoginTests
{
    [Fact]
    public void Response_is_md5_of_challenge_and_padded_password_with_swapped_byte_pairs()
    {
        var challenge = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        const string password = "Selectronic SP PRO";

        var material = challenge.Concat(Encoding.ASCII.GetBytes(password.PadRight(32, ' '))).ToArray();
        var digest = MD5.HashData(material);

        var words = SpProLogin.ComputeResponseWords(challenge, password);

        Assert.Equal(8, words.Length);
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(digest[2 * i], (byte)(words[i] >> 8));
            Assert.Equal(digest[2 * i + 1], (byte)words[i]);
        }

        // On the wire the words go out low byte first, so the digest appears pair-swapped.
        var frame = SpProFrame.BuildWrite(SpProRegisters.LoginChallenge, words);
        Assert.Equal(digest[1], frame[8]);
        Assert.Equal(digest[0], frame[9]);
    }

    [Fact]
    public void Rejects_bad_inputs()
    {
        Assert.Throws<ArgumentException>(() => SpProLogin.ComputeResponseWords(new byte[15], "x"));
        Assert.Throws<ArgumentException>(() => SpProLogin.ComputeResponseWords(new byte[16], new string('x', 33)));
    }
}
