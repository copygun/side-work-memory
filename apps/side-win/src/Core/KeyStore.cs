using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Side.Win.Core;

public interface ISideKeyStore
{
    byte[] MasterKey();
    byte[] RotateMasterKey();
    void SetProviderKey(string reference, string secret);
    string? ProviderKey(string reference);
    (bool Stored, bool Accessible) ProviderKeyStatus(string reference);
    bool AuthorizeProviderKey(string reference);
}

public sealed class KeyStoreException(string message, int error = 0) : Exception(message)
{
    public int Win32Error { get; } = error;
}

/// <summary>
/// Windows Credential Manager (generic credentials, per-user, CRED_PERSIST_LOCAL_MACHINE so the
/// secrets never roam). The blob is additionally wrapped with DPAPI (CurrentUser scope) so a plain
/// credential dump from another account/profile copy cannot use it.
/// Service names match the macOS Keychain items.
/// </summary>
public sealed class WindowsCredentialKeyStore(string? account = null) : ISideKeyStore
{
    public const string MasterService = "local-context-awareness-ledger";
    public const string ProviderService = "side-provider-api-key";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("side-windows-credential-v1");

    private readonly string _account = account ?? Environment.UserName;

    private static string Target(string service, string account) => $"Side/{service}/{account}";

    public byte[] MasterKey()
    {
        var existing = Read(Target(MasterService, _account));
        if (existing is not null)
        {
            if (existing.Length != 32) throw new KeyStoreException("invalid master key");
            return existing;
        }
        var key = RandomNumberGenerator.GetBytes(32);
        Write(Target(MasterService, _account), key);
        var stored = Read(Target(MasterService, _account));
        if (stored is null || stored.Length != 32) throw new KeyStoreException("invalid master key");
        return stored;
    }

    public byte[] RotateMasterKey()
    {
        var replacement = RandomNumberGenerator.GetBytes(32);
        Delete(Target(MasterService, _account));
        Write(Target(MasterService, _account), replacement);
        return replacement;
    }

    public void SetProviderKey(string reference, string secret)
    {
        if (string.IsNullOrEmpty(reference) || string.IsNullOrEmpty(secret))
            throw new KeyStoreException("invalid provider key");
        Write(Target(ProviderService, reference), Encoding.UTF8.GetBytes(secret));
    }

    public string? ProviderKey(string reference)
    {
        if (string.IsNullOrEmpty(reference)) throw new KeyStoreException("invalid provider key");
        var data = Read(Target(ProviderService, reference));
        return data is null ? null : Encoding.UTF8.GetString(data);
    }

    public (bool Stored, bool Accessible) ProviderKeyStatus(string reference)
    {
        if (string.IsNullOrEmpty(reference)) throw new KeyStoreException("invalid provider key");
        try
        {
            var value = ProviderKey(reference);
            return value is null ? (false, false) : (true, value.Length > 0);
        }
        catch (CryptographicException)
        {
            return (true, false);
        }
    }

    public bool AuthorizeProviderKey(string reference) => ProviderKeyStatus(reference).Accessible;

    // --- Credential Manager P/Invoke -------------------------------------------------------

    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    private static byte[]? Read(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return null;
            throw new KeyStoreException("credential read failed", error);
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            var sealedBytes = new byte[credential.CredentialBlobSize];
            if (sealedBytes.Length > 0) Marshal.Copy(credential.CredentialBlob, sealedBytes, 0, sealedBytes.Length);
            try
            {
                return ProtectedData.Unprotect(sealedBytes, Entropy, DataProtectionScope.CurrentUser);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(sealedBytes);
            }
        }
        finally
        {
            CredFree(pointer);
        }
    }

    private static void Write(string target, byte[] secret)
    {
        var sealedBytes = ProtectedData.Protect(secret, Entropy, DataProtectionScope.CurrentUser);
        var blob = Marshal.AllocHGlobal(sealedBytes.Length);
        try
        {
            Marshal.Copy(sealedBytes, 0, blob, sealedBytes.Length);
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = sealedBytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = Environment.UserName,
            };
            if (!CredWrite(ref credential, 0))
                throw new KeyStoreException("credential write failed", Marshal.GetLastWin32Error());
        }
        finally
        {
            var zero = new byte[sealedBytes.Length];
            Marshal.Copy(zero, 0, blob, zero.Length);
            Marshal.FreeHGlobal(blob);
            CryptographicOperations.ZeroMemory(sealedBytes);
        }
    }

    private static void Delete(string target)
    {
        if (CredDelete(target, CredTypeGeneric, 0)) return;
        var error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound) throw new Win32Exception(error);
    }
}
