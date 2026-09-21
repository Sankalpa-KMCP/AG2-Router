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
    private const uint MaxCredentialBlobSize = 2560;

    /// <inheritdoc />
    public Task<WinCredEntry?> ReadCredentialAsync(string target = "gemini:antigravity", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
            if (cred.Type != Advapi32.CRED_TYPE_GENERIC)
            {
                throw new WinCredException($"Credential target returned unsupported type: {cred.Type}");
            }
            if (cred.CredentialBlobSize > MaxCredentialBlobSize)
            {
                throw new WinCredException("Credential blob exceeds the Windows generic-credential bound");
            }
            if (cred.CredentialBlobSize > 0 && cred.CredentialBlob == IntPtr.Zero)
            {
                throw new WinCredException("Credential blob pointer was null for a non-empty credential");
            }
            string? comment = cred.Comment == IntPtr.Zero ? null : Marshal.PtrToStringUni(cred.Comment);
            string? alias = cred.TargetAlias == IntPtr.Zero ? null : Marshal.PtrToStringUni(cred.TargetAlias);
            if (cred.Flags != 0 || cred.AttributeCount != 0 || cred.Attributes != IntPtr.Zero ||
                !string.IsNullOrEmpty(comment) || !string.IsNullOrEmpty(alias))
            {
                throw new WinCredException(
                    "Credential contains metadata unsupported by the safe switch writer; mutation was refused");
            }
            byte[] blob = Array.Empty<byte>();

            if (cred.CredentialBlobSize > 0 && cred.CredentialBlob != IntPtr.Zero)
            {
                int blobSize = checked((int)cred.CredentialBlobSize);
                blob = new byte[blobSize];
                Marshal.Copy(cred.CredentialBlob, blob, 0, blobSize);
            }

            string userName = cred.UserName != IntPtr.Zero
                ? Marshal.PtrToStringUni(cred.UserName) ?? string.Empty
                : string.Empty;

            string targetName = cred.TargetName != IntPtr.Zero
                ? Marshal.PtrToStringUni(cred.TargetName) ?? target
                : target;
            if (!string.Equals(targetName, target, StringComparison.OrdinalIgnoreCase))
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(blob);
                throw new WinCredException("Credential Manager returned an unexpected target");
            }

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
