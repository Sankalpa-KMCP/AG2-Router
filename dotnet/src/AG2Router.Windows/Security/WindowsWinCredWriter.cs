using System;
using System.Runtime.InteropServices;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;

namespace AG2Router.Windows.Security;

/// <summary>
/// Restricted Windows Credential Manager writer.
/// Used strictly when applying credentials during authorized transactions.
/// </summary>
public class WindowsWinCredWriter : IWinCredWriter
{
    /// <inheritdoc />
    public Task<bool> WriteCredentialAsync(WinCredEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (string.IsNullOrWhiteSpace(entry.Target))
        {
            throw new WinCredException("Target name cannot be empty");
        }

        if (entry.Type != Advapi32.CRED_TYPE_GENERIC)
        {
            throw new WinCredException($"Only Generic Credential (type 1) is supported. Received type: {entry.Type}");
        }

        if (entry.Blob == null || entry.Blob.Length == 0)
        {
            throw new WinCredException("Credential blob cannot be null or empty");
        }

        IntPtr targetPtr = IntPtr.Zero;
        IntPtr userPtr = IntPtr.Zero;
        IntPtr blobPtr = IntPtr.Zero;

        try
        {
            targetPtr = Marshal.StringToHGlobalUni(entry.Target);
            userPtr = Marshal.StringToHGlobalUni(entry.UserName ?? string.Empty);
            blobPtr = Marshal.AllocHGlobal(entry.Blob.Length);

            Marshal.Copy(entry.Blob, 0, blobPtr, entry.Blob.Length);

            var cred = new CREDENTIAL
            {
                Flags = 0,
                Type = Advapi32.CRED_TYPE_GENERIC,
                TargetName = targetPtr,
                Comment = IntPtr.Zero,
                LastWritten = default,
                CredentialBlobSize = (uint)entry.Blob.Length,
                CredentialBlob = blobPtr,
                Persist = entry.Persistence != 0 ? entry.Persistence : Advapi32.CRED_PERSIST_LOCAL_MACHINE,
                AttributeCount = 0,
                Attributes = IntPtr.Zero,
                TargetAlias = IntPtr.Zero,
                UserName = userPtr
            };

            if (!Advapi32.CredWrite(ref cred, 0))
            {
                int error = Marshal.GetLastWin32Error();
                throw new WinCredException($"CredWrite failed with Win32 error code: {error}");
            }

            return Task.FromResult(true);
        }
        finally
        {
            if (blobPtr != IntPtr.Zero)
            {
                // Best-effort unmanaged memory zeroing before free
                byte[] zeroes = new byte[entry.Blob.Length];
                Marshal.Copy(zeroes, 0, blobPtr, entry.Blob.Length);
                Marshal.FreeHGlobal(blobPtr);
            }

            if (targetPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(targetPtr);
            }

            if (userPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(userPtr);
            }
        }
    }
}
