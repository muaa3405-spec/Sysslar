using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Tasky.Services.Migration;

/// <summary>
/// Password-based encryption for migration files, with an optional recovery key.
///
/// Version 1 (password only):
///   header  = "TASKYMIG" | 1 | PBKDF2 iterations (int32) | salt (16) | nonce prefix (8)
///   The data key is derived from the password with PBKDF2-HMAC-SHA256.
///
/// Version 2 (password + recovery key):
///   header  = "TASKYMIG" | 2 | nonce prefix (8) | slot count (1) | slots
///   slot    = kind (1) | PBKDF2 iterations (int32) | salt (16) | nonce (12) | wrapped data key (32) | tag (16)
///   The data key is random. Each slot holds it sealed with AES-256-GCM under a key
///   derived from one secret (the password, or the recovery key), so either opens the file.
///
/// Both versions:
///   chunks  = repeated: plaintext length (int32) | ciphertext | GCM tag (16)
///   Each chunk is sealed with AES-256-GCM using nonce = prefix + chunk index. The
///   header, the chunk index and a "last chunk" flag are authenticated with every
///   chunk, so a wrong secret, an edited byte, reordered chunks or a cut-off file are
///   all detected. Nothing about the password or recovery key is stored in the file.
///
/// Files without a recovery key are still written as version 1 so older Sysslar
/// versions can open them.
/// </summary>
public static class MigrationCrypto
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("TASKYMIG");
    private const byte PasswordOnlyVersion = 1;
    private const byte KeySlotsVersion     = 2;

    /// <summary>OWASP 2023 recommendation for PBKDF2-HMAC-SHA256.</summary>
    private const int Iterations = 600_000;
    private const int MinIterations = 100_000;
    private const int MaxIterations = 10_000_000;

    /// <summary>
    /// A recovery key already carries about 119 random bits, so it needs no slow key stretching;
    /// the minimum keeps it inside the range the reader accepts.
    /// </summary>
    private const int RecoveryIterations = MinIterations;

    private const byte PasswordSlot = 1;
    private const byte RecoverySlot = 2;
    private const int  SlotNonceSize = 12;
    private const int  SlotSize = 1 + sizeof(int) + SaltSize + SlotNonceSize + KeySize + TagSize;

    private const int SaltSize        = 16;
    private const int NoncePrefixSize = 8;
    private const int KeySize         = 32;   // AES-256
    private const int TagSize         = 16;
    private const int ChunkSize       = 1024 * 1024;

    private static int V1HeaderSize => Magic.Length + 1 + sizeof(int) + SaltSize + NoncePrefixSize;

    /// <param name="recoveryKey">
    /// Optional second secret (from <see cref="RecoveryKey.Generate"/>) that also opens the file.
    /// </param>
    public static void Encrypt(Stream plaintext, Stream destination, string password, string? recoveryKey = null)
    {
        var noncePrefix = RandomNumberGenerator.GetBytes(NoncePrefixSize);
        byte[] header, key;
        if (recoveryKey is null)
        {
            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            header = BuildV1Header(Iterations, salt, noncePrefix);
            key    = DeriveKey(password, salt, Iterations);
        }
        else
        {
            key = RandomNumberGenerator.GetBytes(KeySize);
            header = BuildV2Header(noncePrefix,
            [
                SealSlot(PasswordSlot, password, Iterations, key),
                SealSlot(RecoverySlot, RecoveryKey.Normalize(recoveryKey)
                                       ?? throw new ArgumentException("Not a valid recovery key.", nameof(recoveryKey)),
                         RecoveryIterations, key),
            ]);
        }
        destination.Write(header);

        try
        {
            using var aes = new AesGcm(key, TagSize);
            var current = new byte[ChunkSize];
            var next    = new byte[ChunkSize];
            int currentLength = ReadFull(plaintext, current);
            long index = 0;
            Span<byte> len = stackalloc byte[sizeof(int)];
            while (true)
            {
                // Look one chunk ahead so the final chunk can be marked as such.
                int nextLength = currentLength == ChunkSize ? ReadFull(plaintext, next) : 0;
                bool isLast = nextLength == 0;

                var cipher = new byte[currentLength];
                var tag    = new byte[TagSize];
                aes.Encrypt(Nonce(noncePrefix, index), current.AsSpan(0, currentLength), cipher, tag,
                            ChunkAad(header, index, isLast));

                BinaryPrimitives.WriteInt32LittleEndian(len, currentLength);
                destination.Write(len);
                destination.Write(cipher);
                destination.Write(tag);

                if (isLast) break;
                (current, next) = (next, current);
                currentLength = nextLength;
                index++;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Decrypts with the password or, for files that have one, the recovery key.</summary>
    /// <exception cref="MigrationFileException">Wrong password, damaged file or not a Tasky migration file.</exception>
    public static void Decrypt(Stream source, Stream plaintext, string passwordOrRecoveryKey)
    {
        var prefix = new byte[Magic.Length + 1];
        if (ReadFull(source, prefix) != prefix.Length || !prefix.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new MigrationFileException("This is not a Sysslar migration file.");

        var (header, noncePrefix, key) = prefix[Magic.Length] switch
        {
            PasswordOnlyVersion => OpenV1(source, prefix, passwordOrRecoveryKey),
            KeySlotsVersion     => OpenV2(source, prefix, passwordOrRecoveryKey),
            _ => throw new MigrationFileException("This migration file was made by a newer version of Sysslar. Update Sysslar and try again."),
        };
        // A version 2 file has already proved the secret by opening a key slot.
        bool secretChecked = prefix[Magic.Length] == KeySlotsVersion;

        try
        {
            using var aes = new AesGcm(key, TagSize);
            var lenBuf = new byte[sizeof(int)];
            var tag    = new byte[TagSize];
            for (long index = 0; ; index++)
            {
                if (ReadFull(source, lenBuf) != lenBuf.Length) throw Damaged();
                int length = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
                if (length < 0 || length > ChunkSize) throw Damaged();

                var cipher = new byte[length];
                if (ReadFull(source, cipher) != length || ReadFull(source, tag) != TagSize) throw Damaged();

                // The last chunk is the one followed by end-of-file.
                bool isLast = source.Position == source.Length;
                var plain = new byte[length];
                try
                {
                    aes.Decrypt(Nonce(noncePrefix, index), cipher, tag, plain, ChunkAad(header, index, isLast));
                }
                catch (AuthenticationTagMismatchException)
                {
                    throw index == 0 && !secretChecked
                        ? new MigrationFileException("Wrong password, or the file is damaged.")
                        : Damaged();
                }
                plaintext.Write(plain);
                if (isLast) return;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static MigrationFileException Damaged() => new("The migration file is damaged or incomplete.");

    /// <summary>Reads the rest of a version 1 header and derives the key from the password.</summary>
    private static (byte[] Header, byte[] NoncePrefix, byte[] Key) OpenV1(Stream source, byte[] prefix, string password)
    {
        var header = new byte[V1HeaderSize];
        prefix.CopyTo(header, 0);
        if (ReadFull(source, header, prefix.Length) != header.Length - prefix.Length) throw Damaged();

        int offset = prefix.Length;
        int iterations = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(offset, sizeof(int)));
        if (iterations < MinIterations || iterations > MaxIterations) throw Damaged();
        offset += sizeof(int);
        var salt        = header.AsSpan(offset, SaltSize).ToArray();
        var noncePrefix = header.AsSpan(offset + SaltSize, NoncePrefixSize).ToArray();
        return (header, noncePrefix, DeriveKey(password, salt, iterations));
    }

    /// <summary>Reads a version 2 header and opens the key slot that the secret fits.</summary>
    private static (byte[] Header, byte[] NoncePrefix, byte[] Key) OpenV2(Stream source, byte[] prefix, string secret)
    {
        var fixedPart = new byte[NoncePrefixSize + 1];
        if (ReadFull(source, fixedPart) != fixedPart.Length) throw Damaged();
        int slotCount = fixedPart[^1];
        if (slotCount is < 1 or > 8) throw Damaged();

        var slots = new byte[slotCount * SlotSize];
        if (ReadFull(source, slots) != slots.Length) throw Damaged();

        var header = new byte[prefix.Length + fixedPart.Length + slots.Length];
        prefix.CopyTo(header, 0);
        fixedPart.CopyTo(header, prefix.Length);
        slots.CopyTo(header, prefix.Length + fixedPart.Length);
        var noncePrefix = fixedPart.AsSpan(0, NoncePrefixSize).ToArray();

        // What was typed may be the password or the recovery key. Only something shaped
        // like a recovery key is tried against the recovery slot, and it is tried first,
        // so a normal password costs one key derivation.
        var recovery = RecoveryKey.Normalize(secret);
        var attempts = new List<(byte Kind, string Secret)>();
        if (recovery is not null) attempts.Add((RecoverySlot, recovery));
        attempts.Add((PasswordSlot, secret));

        bool hasRecovery = false;
        foreach (var (kind, value) in attempts)
        {
            for (int i = 0; i < slotCount; i++)
            {
                var slot = slots.AsSpan(i * SlotSize, SlotSize);
                if (slot[0] == RecoverySlot) hasRecovery = true;
                if (slot[0] != kind) continue;
                if (TryOpenSlot(slot, value) is { } key) return (header, noncePrefix, key);
            }
        }
        throw new MigrationFileException(hasRecovery
            ? "Wrong password or recovery key, or the file is damaged."
            : "Wrong password, or the file is damaged.");
    }

    private static byte[] SealSlot(byte kind, string secret, int iterations, byte[] dataKey)
    {
        var slot  = new byte[SlotSize];
        var salt  = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(SlotNonceSize);
        slot[0] = kind;
        BinaryPrimitives.WriteInt32LittleEndian(slot.AsSpan(1), iterations);
        int offset = 1 + sizeof(int);
        salt.CopyTo(slot, offset);   offset += SaltSize;
        nonce.CopyTo(slot, offset);  offset += SlotNonceSize;

        var wrapKey = DeriveKey(secret, salt, iterations);
        try
        {
            using var aes = new AesGcm(wrapKey, TagSize);
            aes.Encrypt(nonce, dataKey, slot.AsSpan(offset, KeySize), slot.AsSpan(offset + KeySize, TagSize),
                        SlotAad(kind));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrapKey);
        }
        return slot;
    }

    /// <summary>The data key in this slot, or null when the secret doesn't open it.</summary>
    private static byte[]? TryOpenSlot(ReadOnlySpan<byte> slot, string secret)
    {
        int iterations = BinaryPrimitives.ReadInt32LittleEndian(slot.Slice(1, sizeof(int)));
        if (iterations < MinIterations || iterations > MaxIterations) throw Damaged();
        int offset = 1 + sizeof(int);
        var salt  = slot.Slice(offset, SaltSize).ToArray();       offset += SaltSize;
        var nonce = slot.Slice(offset, SlotNonceSize).ToArray();  offset += SlotNonceSize;

        var wrapKey = DeriveKey(secret, salt, iterations);
        try
        {
            using var aes = new AesGcm(wrapKey, TagSize);
            var dataKey = new byte[KeySize];
            aes.Decrypt(nonce, slot.Slice(offset, KeySize), slot.Slice(offset + KeySize, TagSize), dataKey,
                        SlotAad(slot[0]));
            return dataKey;
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrapKey);
        }
    }

    private static byte[] SlotAad(byte kind) => [.. Magic, KeySlotsVersion, kind];

    private static byte[] DeriveKey(string password, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations,
                                     HashAlgorithmName.SHA256, KeySize);

    private static byte[] BuildV2Header(byte[] noncePrefix, byte[][] slots)
    {
        var header = new byte[Magic.Length + 1 + NoncePrefixSize + 1 + slots.Length * SlotSize];
        Magic.CopyTo(header, 0);
        int offset = Magic.Length;
        header[offset++] = KeySlotsVersion;
        noncePrefix.CopyTo(header, offset);  offset += NoncePrefixSize;
        header[offset++] = (byte)slots.Length;
        foreach (var slot in slots) { slot.CopyTo(header, offset); offset += SlotSize; }
        return header;
    }

    private static byte[] BuildV1Header(int iterations, byte[] salt, byte[] noncePrefix)
    {
        var header = new byte[V1HeaderSize];
        Magic.CopyTo(header, 0);
        header[Magic.Length] = PasswordOnlyVersion;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(Magic.Length + 1), iterations);
        salt.CopyTo(header, Magic.Length + 1 + sizeof(int));
        noncePrefix.CopyTo(header, Magic.Length + 1 + sizeof(int) + SaltSize);
        return header;
    }

    private static byte[] Nonce(byte[] prefix, long index)
    {
        var nonce = new byte[NoncePrefixSize + sizeof(uint)];
        prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(NoncePrefixSize), checked((uint)index));
        return nonce;
    }

    private static byte[] ChunkAad(byte[] header, long index, bool isLast)
    {
        var aad = new byte[header.Length + sizeof(long) + 1];
        header.CopyTo(aad, 0);
        BinaryPrimitives.WriteInt64BigEndian(aad.AsSpan(header.Length), index);
        aad[^1] = isLast ? (byte)1 : (byte)0;
        return aad;
    }

    private static int ReadFull(Stream stream, byte[] buffer, int start = 0)
    {
        int total = start, read;
        while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
            total += read;
        return total - start;
    }
}

/// <summary>
/// The optional recovery key for a migration file: 24 random characters (about 119 bits) shown in
/// groups of four, e.g. "K7QM-2XPA-…". Look-alike characters (0/O, 1/I/L) are left out so it
/// can be copied by hand from paper.
/// </summary>
public static class RecoveryKey
{
    /// <summary>A–Z and 2–9 without I, L and O: 31 characters, so a key holds about 119 random bits.</summary>
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    public  const int    Length   = 24;
    private const int    Group    = 4;

    public static string Generate()
    {
        var chars = new char[Length];
        for (int i = 0; i < Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return Format(new string(chars));
    }

    /// <summary>
    /// The key in its canonical form (upper case, without dashes or spaces), or null when the
    /// text isn't shaped like a recovery key.
    /// </summary>
    public static string? Normalize(string text)
    {
        var sb = new StringBuilder(Length);
        foreach (var c in text)
        {
            if (c is '-' or ' ' or '\t' or '\r' or '\n') continue;
            var upper = char.ToUpperInvariant(c);
            if (!Alphabet.Contains(upper)) return null;
            sb.Append(upper);
        }
        return sb.Length == Length ? sb.ToString() : null;
    }

    private static string Format(string key)
        => string.Join('-', Enumerable.Range(0, key.Length / Group).Select(i => key.Substring(i * Group, Group)));
}

/// <summary>A migration file could not be opened; the message is safe to show to the user.</summary>
public sealed class MigrationFileException(string message) : Exception(message);
