using System.Runtime.InteropServices;
using System.Text;

namespace Spellbook;

/// <summary>API keys and tokens live in Windows Credential Manager, never in the ini.</summary>
internal static class Credentials
{
    const uint CRED_TYPE_GENERIC = 1, CRED_PERSIST_LOCAL_MACHINE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredWriteW([In] ref CREDENTIAL credential, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredDeleteW(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] static extern void CredFree(IntPtr buffer);

    static string Target(string key) => Brand.Name + "/" + key;

    public static string? Get(string key)
    {
        if (!CredReadW(Target(key), CRED_TYPE_GENERIC, 0, out var p)) return null;
        try
        {
            var c = Marshal.PtrToStructure<CREDENTIAL>(p);
            if (c.CredentialBlobSize == 0 || c.CredentialBlob == IntPtr.Zero) return null;
            var bytes = new byte[c.CredentialBlobSize];
            Marshal.Copy(c.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally { CredFree(p); }
    }

    public static bool Has(string key) => !string.IsNullOrEmpty(Get(key));

    public static void Set(string key, string secret)
    {
        if (string.IsNullOrEmpty(secret)) { Delete(key); return; }
        var bytes = Encoding.Unicode.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var c = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = Target(key),
                Comment = Brand.Name + " API credential",
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = Environment.UserName,
            };
            if (!CredWriteW(ref c, 0)) throw new InvalidOperationException("Credential Manager write failed (error " + Marshal.GetLastWin32Error() + ")");
        }
        finally { Marshal.FreeHGlobal(blob); }
    }

    public static void Delete(string key) => CredDeleteW(Target(key), CRED_TYPE_GENERIC, 0);
}
