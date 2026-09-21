using System;
using System.Runtime.InteropServices;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;

namespace AG2Router.Windows.Security;

/// <summary>
/// Strictly read-only Windows Credential Manager reader.
/// Uses native Advapi32.dll CredReadW / CredFree.
/// Does not contain or expose any credential write, edit, or delete functionality.
/// </summary>
public class WindowsWinCredReader : IWinCredReader
{
    private readonly string _defaultTarget;

    public WindowsWinCredReader(string defaultTarget = "gemini:antigravity")
    {
        _defaultTarget = defaultTarget;
    }

    /// <inheritdoc />
    public Task<WinCredEntry?> ReadCredentialAsync(string target = "gemini:antigravity", CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new WinCredException("Target name must be a non-empty string");
        }

        IntPtr credPtr = IntPtr.Zero;
        try
        {
            if (!Advapi32.CredRead(target, Advapi32.CRED_TYPE_GENERIC, 0, out credPtr))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == Advapi32.ERROR_NOT_FOUND)
                {
                    return Task.FromResult<WinCredEntry?>(null);
                }

                throw new WinCredException($"CredRead failed with Win32 error code: {error}");
            }

            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            byte[] blob = Array.Empty<byte>();

            if (cred.CredentialBlobSize > 0 && cred.CredentialBlob != IntPtr.Zero)
            {
                blob = new byte[cred.CredentialBlobSize];
                Marshal.Copy(cred.CredentialBlob, blob, 0, (int)cred.CredentialBlobSize);
            }

            string userName = cred.UserName != IntPtr.Zero
                ? Marshal.PtrToStringUni(cred.UserName) ?? string.Empty
                : string.Empty;

            string targetName = cred.TargetName != IntPtr.Zero
                ? Marshal.PtrToStringUni(cred.TargetName) ?? target
                : target;

            var entry = new WinCredEntry(
                Target: targetName,
                Type: cred.Type,
                UserName: userName,
                Persistence: cred.Persist,
                Blob: blob
            );

            return Task.FromResult<WinCredEntry?>(entry);
        }
        finally
        {
            if (credPtr != IntPtr.Zero)
            {
                Advapi32.CredFree(credPtr);
            }
        }
    }
}
