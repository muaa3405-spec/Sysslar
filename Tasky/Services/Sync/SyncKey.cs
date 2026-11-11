using System.Security.Cryptography;
using System.Text;

namespace Tasky.Services.Sync;

/// <summary>
/// The sync encryption key: 256 random bits, shown to the user as a code such as
/// <c>K7QF2X-9MH4TD-…</c> (base32, 9 groups of 6, the last two characters a checksum so typos are
/// caught). It is random, not a chosen password, so it can't be guessed and needs no slow key
/// stretching. Purpose keys are derived from it with HKDF, so one secret never encrypts two kinds of data.
/// </summary>
public sealed class SyncKey
{
    private const int SecretSize = 32;
    private readonly byte[] _secret;

    private SyncKey(byte[] secret) => _secret = secret;

    public static SyncKey Generate() => new(RandomNumberGenerator.GetBytes(SecretSize));

    public static SyncKey FromSecret(byte[] secret)
        => secret.Length == SecretSize ? new SyncKey([.. secret]) : throw new ArgumentException("A sync key is 32 bytes");

    public byte[] Secret => [.. _secret];

    /// <summary>8 bytes that identify the key without revealing it. Stored in the clear in every sync file.</summary>
    public byte[] Fingerprint => Derive("fingerprint", 8);

    public string FingerprintHex => Convert.ToHexStringLower(Fingerprint);

    /// <summary>Key for files of one scope: a workspace's sync id, or "space" for the shared files.</summary>
    public byte[] FileKey(string scope) => Derive("file:" + scope, 32);

    /// <summary>Key that turns an attachment's content hash into its file name in the sync folder.</summary>
    public byte[] BlobNameKey(string scope) => Derive("blob-name:" + scope, 32);

    private byte[] Derive(string purpose, int length)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, _secret, length, salt: [], info: Encoding.UTF8.GetBytes("sysslar-sync/v1/" + purpose));

    // ── The code the user sees and types ─────────────────────────────────

    /// <summary>The key as the user sees it: 54 characters in 9 groups of 6.</summary>
    public string ToCode()
    {
        var text = SyncIds.Base32(_secret) + Checksum(_secret);
        return string.Join("-", Enumerable.Range(0, text.Length / 6).Select(i => text.Substring(i * 6, 6)));
    }

    /// <summary>
    /// Reads a typed or pasted code. Spaces, dashes and case don't matter, and the digits people
    /// confuse with letters (0, 1, 8) are read as O, I and B. Returns null when the code is wrong.
    /// </summary>
    public static SyncKey? TryParseCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var text = new string(code.ToUpperInvariant()
            .Where(char.IsLetterOrDigit)
            .Select(c => c switch { '0' => 'O', '1' => 'I', '8' => 'B', _ => c })
            .ToArray());
        if (text.Length != 54) return null;
        var secret = SyncIds.FromBase32(text[..52]);
        if (secret is null || secret.Length != SecretSize) return null;
        return Checksum(secret) == text[52..] ? new SyncKey(secret) : null;
    }

    private static string Checksum(byte[] secret)
        => SyncIds.Base32(SHA256.HashData([.. Encoding.ASCII.GetBytes("sysslar-code"), .. secret]))[..2];
}
