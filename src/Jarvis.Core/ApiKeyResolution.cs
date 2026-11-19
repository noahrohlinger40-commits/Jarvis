// ApiKeyResolution.cs: finds an API key (for the model, speech or voice services) without keeping secrets in the
// settings file. ApiKeyResolver is called from ModelGateway.cs and the speech/voice code; WindowsCredentialStore is
// the small Windows Credential Manager wrapper it uses. A local llama-server needs no key, so this often returns empty.
using System.Runtime.InteropServices;
using System.Text;

namespace Jarvis.Core;

// Looks for a key in three places, in this order: the value typed in settings, Windows Credential Manager, then an environment variable.
public static class ApiKeyResolver
{
    // Returns the first non-empty key found, or an empty string when none is configured.
    public static string Resolve(string directValue, string credentialTarget, string envVar)
    {
        if (!string.IsNullOrWhiteSpace(directValue))
        {
            return directValue.Trim();
        }

        if (WindowsCredentialStore.TryReadGenericSecret(credentialTarget, out var credentialValue))
        {
            return credentialValue;
        }

        if (string.IsNullOrWhiteSpace(envVar))
        {
            return string.Empty;
        }

        return Environment.GetEnvironmentVariable(envVar)?.Trim() ?? string.Empty;
    }
}

// Reads and writes "generic" entries in Windows Credential Manager (the same vault Windows uses for saved passwords) through advapi32.dll.
internal static class WindowsCredentialStore
{
    // Windows constant CRED_TYPE_GENERIC: an app-defined secret rather than a network login.
    private const int CredTypeGeneric = 1;

    // Reads the secret saved under the given name; returns false (and an empty secret) if it is missing or unreadable.
    public static bool TryReadGenericSecret(string target, out string secret)
    {
        secret = string.Empty;

        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        if (!CredReadW(target.Trim(), CredTypeGeneric, 0, out var credentialPointer))
        {
            // Not found (ERROR_NOT_FOUND) and every other failure both mean "no usable secret".
            return false;
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(credentialPointer);

            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize <= 0)
            {
                return false;
            }

            // The secret is stored as UTF-16 bytes, so copy them out and decode as Unicode, trimming trailing null characters.
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            secret = Encoding.Unicode.GetString(bytes).TrimEnd('\0').Trim();
            return !string.IsNullOrWhiteSpace(secret);
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    // Saves a secret under the given name for the current Windows user.
    public static bool TryWriteGenericSecret(string target, string secret)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var bytes = Encoding.Unicode.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(bytes.Length);

        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = 2, // CRED_PERSIST_LOCAL_MACHINE: this user, this PC, kept across restarts
                UserName = Environment.UserName
            };

            return CredWriteW(ref credential, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    // Native Windows calls (P/Invoke): write a credential, read one, and free the memory Windows allocated for a read.
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWriteW(ref Credential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr credentialPtr);

    // Mirrors the Windows CREDENTIALW struct; field order and types must match it exactly or the native call reads garbage.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }
}
