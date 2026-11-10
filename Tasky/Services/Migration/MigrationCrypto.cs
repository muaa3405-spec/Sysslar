using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Tasky.Services.Migration;

/// <summary>
/// Password-based encryption for migration files.
///
/// Layout:
///   header  = "TASKYMIG" | version (1 byte) | PBKDF2 iterations (int32) | salt (16) | nonce prefix (8)
///   chunks  = repeated: plaintext length (int32) | ciphertext | GCM tag (16)
///
/// The key is derived from the password with PBKDF2-HMAC-SHA256. Each chunk is
/// sealed with AES-256-GCM using nonce = prefix + chunk index. The header, the
/// chunk index and a "last chunk" flag are authenticated with every chunk, so a
/// wrong password, an edited byte, reordered chunks or a cut-off file are all
/// detected. Nothing about the password is stored in the file.
/// </summary>
public static class MigrationCrypto
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("TASKYMIG");
    private const byte FormatVersion = 1;

    /// <summary>OWASP 2023 recommendation for PBKDF2-HMAC-SHA256.</summary>
    private const int Iterations = 600_000;
    private const int MinIterations = 100_000;
    private const int MaxIterations = 10_000_000;

    private const int SaltSize        = 16;
    private const int NoncePrefixSize = 8;
    private const int KeySize         = 32;   // AES-256
    private const int TagSize         = 16;
    private const int ChunkSize       = 1024 * 1024;

    private static int HeaderSize => Magic.Length + 1 + sizeof(int) + SaltSize + NoncePrefixSize;

    public static void Encrypt(Stream plaintext, Stream destination, string password)
    {
        var salt        = RandomNumberGenerator.GetBytes(SaltSize);
        var noncePrefix = RandomNumberGenerator.GetBytes(NoncePrefixSize);
        var header      = BuildHeader(Iterations, salt, noncePrefix);
        destination.Write(header);

        var key = DeriveKey(password, salt, Iterations);
        try
        {
            using var aes = new AesGcm(key, TagSize);
            var current = new byte[ChunkSize];
            var next    = new byte[ChunkSize];
            int currentLength = ReadFull(plaintext, current);
            long index = 0;
            while (true)
            {
                // Look one chunk ahead so the final chunk can be marked as such.
                int nextLength = currentLength == ChunkSize ? ReadFull(plaintext, next) : 0;
                bool isLast = nextLength == 0;

                var cipher = new byte[currentLength];
                var tag    = new byte[TagSize];
                aes.Encrypt(Nonce(noncePrefix, index), current.AsSpan(0, currentLength), cipher, tag,
                            ChunkAad(header, index, isLast));

                Span<byte> len = stackalloc byte[sizeof(int)];
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

    /// <exception cref="MigrationFileException">Wrong password, damaged file or not a Tasky migration file.</exception>
    public static void Decrypt(Stream source, Stream plaintext, string password)
    {
        var header = new byte[HeaderSize];
        if (ReadFull(source, header) != HeaderSize || !header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new MigrationFileException("This is not a Sysslar migration file.");
        if (header[Magic.Length] != FormatVersion)
            throw new MigrationFileException("This migration file was made by a newer version of Sysslar. Update Sysslar and try again.");

        int offset = Magic.Length + 1;
        int iterations = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(offset, sizeof(int)));
        if (iterations < MinIterations || iterations > MaxIterations)
            throw new MigrationFileException("The migration file is damaged.");
        offset += sizeof(int);
        var salt        = header.AsSpan(offset, SaltSize).ToArray();
        var noncePrefix = header.AsSpan(offset + SaltSize, NoncePrefixSize).ToArray();

        var key = DeriveKey(password, salt, iterations);
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
                    throw index == 0
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

    private static byte[] DeriveKey(string password, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations,
                                     HashAlgorithmName.SHA256, KeySize);

    private static byte[] BuildHeader(int iterations, byte[] salt, byte[] noncePrefix)
    {
        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        header[Magic.Length] = FormatVersion;
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

    private static int ReadFull(Stream stream, byte[] buffer)
    {
        int total = 0, read;
        while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
            total += read;
        return total;
    }
}

/// <summary>A migration file could not be opened; the message is safe to show to the user.</summary>
public sealed class MigrationFileException(string message) : Exception(message);
