using System.Runtime.InteropServices;
using System.Text;

namespace TokenConsumptionMonitoring.Services;

/// <summary>Windows 凭据管理器（opencode OAuth token 存储）。</summary>
public static class CredentialStore
{
    public const string OAuthTarget = AppIdentity.OAuthTarget;

    private const int CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_ENTERPRISE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    public static void SaveSecret(string target, string secret)
    {
        var bytes = Encoding.Unicode.GetBytes(secret);
        var cred = new CREDENTIAL
        {
            Type = CRED_TYPE_GENERIC,
            TargetName = Marshal.StringToCoTaskMemUni(target),
            CredentialBlobSize = (uint)bytes.Length,
            CredentialBlob = Marshal.AllocCoTaskMem(bytes.Length),
            Persist = CRED_PERSIST_ENTERPRISE,
            UserName = Marshal.StringToCoTaskMemUni(Environment.UserName),
        };
        try
        {
            Marshal.Copy(bytes, 0, cred.CredentialBlob, bytes.Length);
            if (!CredWrite(ref cred, 0))
                throw new InvalidOperationException($"CredWrite failed: Win32 {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            Marshal.FreeCoTaskMem(cred.TargetName);
            Marshal.FreeCoTaskMem(cred.CredentialBlob);
            Marshal.FreeCoTaskMem(cred.UserName);
        }
    }

    public static bool TryReadSecret(string target, out string? secret)
    {
        secret = null;
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var ptr)) return false;
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0) return false;
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, (int)cred.CredentialBlobSize);
            secret = Encoding.Unicode.GetString(bytes);
            return true;
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public static void Delete(string target) => CredDelete(target, CRED_TYPE_GENERIC, 0);
}
