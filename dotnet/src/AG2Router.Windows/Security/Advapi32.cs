using System;
using System.Runtime.InteropServices;

namespace AG2Router.Windows.Security;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct CREDENTIAL
{
    public uint Flags;
    public uint Type;                   // 1 = CRED_TYPE_GENERIC
    public IntPtr TargetName;           // LPWSTR
    public IntPtr Comment;              // LPWSTR
    public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
    public uint CredentialBlobSize;
    public IntPtr CredentialBlob;       // LPBYTE
    public uint Persist;                // 2 = CRED_PERSIST_LOCAL_MACHINE
    public uint AttributeCount;
    public IntPtr Attributes;
    public IntPtr TargetAlias;          // LPWSTR
    public IntPtr UserName;             // LPWSTR
}

internal static class Advapi32
{
    public const uint CRED_TYPE_GENERIC = 1;
    public const uint CRED_PERSIST_LOCAL_MACHINE = 2;
    public const int ERROR_NOT_FOUND = 1168;

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CredRead(
        string target,
        uint type,
        uint reservedFlag,
        out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CredWrite(
        [In] ref CREDENTIAL credential,
        uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CredDelete(
        string target,
        uint type,
        uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree", SetLastError = false)]
    public static extern void CredFree(IntPtr buffer);
}
