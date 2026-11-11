using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tasky.Services.Sync;

/// <summary>What a sync file holds. Part of the authenticated header, so one kind can't pass as another.</summary>
public enum SyncFileKind : byte
{
    Snapshot  = 1,   // one computer's copy of one workspace
    Device    = 2,   // one computer's name and the workspaces it syncs
    KeyCheck  = 3,   // proves which key the folder uses
    KeyChange = 4,   // "the key was changed", sealed with the old key
    Blob      = 5,   // an attachment's content
}

/// <summary>Thrown when a sync file can't be opened: wrong key, damaged, cut off or edited.</summary>
public sealed class SyncFileException(string message) : Exception(message);

/// <summary>
/// Sealing and opening sync files with AES-256-GCM.
///
///   header = "SYSSYNC1" | version (1) | kind (1) | key fingerprint (8) | nonce (12)
///   body   = ciphertext | GCM tag (16)
///
/// The header and a context string (which workspace / computer / attachment the file is for, taken
/// from where it lives in the folder) are authenticated with the content, so a file that is edited,
/// cut off, renamed onto another computer's slot or moved into another workspace is refused.
/// The nonce is random for every file, so a long-lived key never repeats one.
/// The key fingerprint is in the clear so Sysslar can tell "made with another key" from "damaged".
/// </summary>
public static class SyncCrypto
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SYSSYNC1");
    private const byte Version   = 1;
    private const int  NonceSize = 12;
    private const int  TagSize   = 16;
    private const int  FingerprintSize = 8;
    private static int HeaderSize => Magic.Length + 1 + 1 + FingerprintSize + NonceSize;

    public static byte[] Seal(SyncKey key, string scope, SyncFileKind kind, string context, ReadOnlySpan<byte> plaintext)
    {
        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        header[Magic.Length]     = Version;
        header[Magic.Length + 1] = (byte)kind;
        key.Fingerprint.CopyTo(header, Magic.Length + 2);
        RandomNumberGenerator.Fill(header.AsSpan(Magic.Length + 2 + FingerprintSize, NonceSize));

        var output = new byte[HeaderSize + plaintext.Length + TagSize];
        header.CopyTo(output, 0);
        using var aes = new AesGcm(key.FileKey(scope), TagSize);
        aes.Encrypt(header.AsSpan(Magic.Length + 2 + FingerprintSize, NonceSize), plaintext,
                    output.AsSpan(HeaderSize, plaintext.Length), output.AsSpan(HeaderSize + plaintext.Length, TagSize),
                    Aad(header, context));
        return output;
    }

    public static byte[] Open(SyncKey key, string scope, SyncFileKind kind, string context, ReadOnlySpan<byte> file)
    {
        if (ReadFingerprint(file) is not { } fingerprint) throw new SyncFileException("Not a Sysslar sync file.");
        if (file[Magic.Length + 1] != (byte)kind) throw new SyncFileException("This sync file is of another kind.");
        if (fingerprint != key.FingerprintHex) throw new SyncFileException("This sync file was made with another key.");
        if (file.Length < HeaderSize + TagSize) throw new SyncFileException("This sync file is cut off.");

        var header = file[..HeaderSize].ToArray();
        int length = file.Length - HeaderSize - TagSize;
        var plaintext = new byte[length];
        try
        {
            using var aes = new AesGcm(key.FileKey(scope), TagSize);
            aes.Decrypt(header.AsSpan(Magic.Length + 2 + FingerprintSize, NonceSize), file.Slice(HeaderSize, length),
                        file.Slice(HeaderSize + length, TagSize), plaintext, Aad(header, context));
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new SyncFileException("This sync file is damaged, incomplete or was changed.");
        }
        return plaintext;
    }

    /// <summary>The key fingerprint (hex) of a sync file, or null when it isn't one.</summary>
    public static string? ReadFingerprint(ReadOnlySpan<byte> file)
    {
        if (file.Length < HeaderSize || !file[..Magic.Length].SequenceEqual(Magic) || file[Magic.Length] != Version) return null;
        return Convert.ToHexStringLower(file.Slice(Magic.Length + 2, FingerprintSize));
    }

    private static byte[] Aad(byte[] header, string context) => [.. header, .. Encoding.UTF8.GetBytes(context)];

    // ── JSON content ─────────────────────────────────────────────────────

    public static byte[] SealJson<T>(SyncKey key, string scope, SyncFileKind kind, string context, T value)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            JsonSerializer.Serialize(gzip, value);
        return Seal(key, scope, kind, context, buffer.ToArray());
    }

    public static T OpenJson<T>(SyncKey key, string scope, SyncFileKind kind, string context, ReadOnlySpan<byte> file)
    {
        var plain = Open(key, scope, kind, context, file);
        try
        {
            using var gzip = new GZipStream(new MemoryStream(plain), CompressionMode.Decompress);
            return JsonSerializer.Deserialize<T>(gzip) ?? throw new SyncFileException("This sync file is empty.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new SyncFileException("This sync file couldn't be read.");
        }
    }
}
