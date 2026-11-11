using System.Security.Cryptography;
using System.Text;

namespace Tasky.Services.Sync;

/// <summary>Random ids and base32 text, safe as file names on Windows and macOS (lowercase a–z, 2–7).</summary>
public static class SyncIds
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>A new random 128-bit id, 26 lowercase base32 characters.</summary>
    public static string New() => Base32(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>An id derived from <paramref name="text"/>: the same text gives the same id on every computer.</summary>
    public static string FromText(string text)
        => Base32(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..26].ToLowerInvariant();

    public static string Base32(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits  += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    /// <summary>Decodes base32 (upper or lower case). Returns null on any character outside the alphabet.</summary>
    public static byte[]? FromBase32(string text)
    {
        var output = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var ch in text.ToUpperInvariant())
        {
            int value = Alphabet.IndexOf(ch);
            if (value < 0) return null;
            buffer = (buffer << 5) | value;
            bits  += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return [.. output];
    }
}
