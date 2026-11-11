using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Tasky.Services.Sync;

/// <summary>Keeps the sync key on this computer, protected by the user's account.</summary>
public interface ISyncSecretStore
{
    byte[]? Read(string account);
    void Write(string account, byte[] secret);
    void Delete(string account);
}

public static class SyncSecretStores
{
    /// <summary>Windows: DPAPI. macOS: the login keychain. Sandboxed test runs: a plain file in the sandbox.</summary>
    public static ISyncSecretStore ForThisComputer(string folder)
    {
        // Test runs (TASKY_DATA_ROOT) must never touch the real keychain, and CI has none to unlock.
        if (AppPaths.IsSandboxed) return new SandboxSecretStore(folder);
        if (OperatingSystem.IsWindows()) return new DpapiSecretStore(folder);
        if (OperatingSystem.IsMacOS())   return new KeychainSecretStore();
        throw new PlatformNotSupportedException();
    }
}

/// <summary>Sandboxed test runs only: the key as a plain file inside the throw-away sandbox folder.</summary>
internal sealed class SandboxSecretStore(string folder) : ISyncSecretStore
{
    private string FileFor(string account) => Path.Combine(folder, $"test-key-{account}.txt");
    public byte[]? Read(string account) => File.Exists(FileFor(account)) ? SyncIds.FromBase32(File.ReadAllText(FileFor(account))) : null;
    public void Write(string account, byte[] secret) { Directory.CreateDirectory(folder); File.WriteAllText(FileFor(account), SyncIds.Base32(secret)); }
    public void Delete(string account) { if (File.Exists(FileFor(account))) File.Delete(FileFor(account)); }
}

/// <summary>
/// Windows: the key is encrypted with DPAPI for the current user (only this Windows account on this
/// computer, or one it roams to, can unlock it) and kept as a small file in the local sync folder.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DpapiSecretStore(string folder) : ISyncSecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Sysslar sync key");

    private string FileFor(string account) => Path.Combine(folder, $"key-{account}.dpapi");

    public byte[]? Read(string account)
    {
        var file = FileFor(account);
        if (!File.Exists(file)) return null;
        return Unprotect(File.ReadAllBytes(file));
    }

    public void Write(string account, byte[] secret)
    {
        Directory.CreateDirectory(folder);
        var file = FileFor(account);
        File.WriteAllBytes(file + ".tmp", Protect(secret));
        File.Move(file + ".tmp", file, overwrite: true);
    }

    public void Delete(string account)
    {
        var file = FileFor(account);
        if (File.Exists(file)) File.Delete(file);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, ref DataBlob entropy,
                                                IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy,
                                                  IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr mem);

    private const int UiForbidden = 0x1;

    private static byte[] Protect(byte[] data) => Run(data, protect: true);
    private static byte[]? Unprotect(byte[] data) { try { return Run(data, protect: false); } catch { return null; } }

    private static byte[] Run(byte[] data, bool protect)
    {
        var input   = Pin(data, out var inputHandle);
        var entropy = Pin(Entropy, out var entropyHandle);
        try
        {
            bool ok = protect
                ? CryptProtectData(ref input, "Sysslar sync key", ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok) throw new InvalidOperationException($"DPAPI failed ({Marshal.GetLastWin32Error()})");
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally { inputHandle.Free(); entropyHandle.Free(); }
    }

    private static DataBlob Pin(byte[] data, out GCHandle handle)
    {
        handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        return new DataBlob { Size = data.Length, Data = handle.AddrOfPinnedObject() };
    }
}

/// <summary>macOS: the key is a generic password in the user's login keychain ("Sysslar Sync").</summary>
[SupportedOSPlatform("macos")]
internal sealed class KeychainSecretStore : ISyncSecretStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string Service = "Sysslar Sync";
    private const int ItemNotFound = -25300;

    [DllImport(Security)]
    private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLength, byte[] service,
        uint accountLength, byte[] account, uint passwordLength, byte[] password, IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainFindGenericPassword(IntPtr keychain, uint serviceLength, byte[] service,
        uint accountLength, byte[] account, out uint passwordLength, out IntPtr password, out IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainItemModifyAttributesAndData(IntPtr item, IntPtr attributes, uint length, byte[] data);

    [DllImport(Security)] private static extern int SecKeychainItemFreeContent(IntPtr attributes, IntPtr data);
    [DllImport(Security)] private static extern int SecKeychainItemDelete(IntPtr item);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr cf);

    private static readonly byte[] ServiceBytes = Encoding.UTF8.GetBytes(Service);

    public byte[]? Read(string account)
    {
        var acc = Encoding.UTF8.GetBytes(account);
        int status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)ServiceBytes.Length, ServiceBytes, (uint)acc.Length, acc,
                                                    out uint length, out IntPtr data, out IntPtr item);
        if (status == ItemNotFound) return null;
        if (status != 0) throw new InvalidOperationException($"The keychain couldn't be read (error {status}).");
        try
        {
            var text = new byte[length];
            Marshal.Copy(data, text, 0, (int)length);
            return SyncIds.FromBase32(Encoding.ASCII.GetString(text));
        }
        finally
        {
            SecKeychainItemFreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero) CFRelease(item);
        }
    }

    public void Write(string account, byte[] secret)
    {
        var acc  = Encoding.UTF8.GetBytes(account);
        var data = Encoding.ASCII.GetBytes(SyncIds.Base32(secret));
        int status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)ServiceBytes.Length, ServiceBytes, (uint)acc.Length, acc,
                                                    out _, out IntPtr existing, out IntPtr item);
        if (status == 0)
        {
            SecKeychainItemFreeContent(IntPtr.Zero, existing);
            try { status = SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)data.Length, data); }
            finally { CFRelease(item); }
        }
        else
        {
            status = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)ServiceBytes.Length, ServiceBytes, (uint)acc.Length, acc,
                                                   (uint)data.Length, data, IntPtr.Zero);
        }
        if (status != 0) throw new InvalidOperationException($"The keychain couldn't be written (error {status}).");
    }

    public void Delete(string account)
    {
        var acc = Encoding.UTF8.GetBytes(account);
        int status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)ServiceBytes.Length, ServiceBytes, (uint)acc.Length, acc,
                                                    out _, out IntPtr data, out IntPtr item);
        if (status != 0) return;
        SecKeychainItemFreeContent(IntPtr.Zero, data);
        try { SecKeychainItemDelete(item); }
        finally { CFRelease(item); }
    }
}
