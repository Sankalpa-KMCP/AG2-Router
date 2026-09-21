using System;
using System.Security.Cryptography;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;

namespace AG2Router.Windows.Security;

/// <summary>
/// Native Windows Data Protection API (DPAPI) provider.
/// Uses System.Security.Cryptography.ProtectedData scoped strictly to CurrentUser.
/// Ensures 100% binary compatibility with ciphertexts created by PowerShell ProtectedData.
/// </summary>
public class WindowsDpapiProvider : IDpapiProvider
{
    /// <inheritdoc />
    public Task<byte[]> EncryptAsync(byte[] plaintext, CancellationToken cancellationToken = default)
    {
        if (plaintext == null || plaintext.Length == 0)
        {
            throw new DpapiException("Cannot encrypt empty or null payload");
        }

        try
        {
            byte[] ciphertext = ProtectedData.Protect(
                plaintext,
                optionalEntropy: null,
                DataProtectionScope.CurrentUser
            );

            return Task.FromResult(ciphertext);
        }
        catch (CryptographicException ex)
        {
            throw new DpapiException($"DPAPI encryption failed: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            throw new DpapiException($"Unexpected DPAPI encryption error: {ex.Message}", ex);
        }
        finally
        {
            // Best-effort memory hygiene: zero out the plaintext buffer
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <inheritdoc />
    public Task<byte[]> DecryptAsync(byte[] ciphertext, CancellationToken cancellationToken = default)
    {
        if (ciphertext == null || ciphertext.Length == 0)
        {
            throw new DpapiException("Cannot decrypt empty or null ciphertext");
        }

        try
        {
            byte[] plaintext = ProtectedData.Unprotect(
                ciphertext,
                optionalEntropy: null,
                DataProtectionScope.CurrentUser
            );

            return Task.FromResult(plaintext);
        }
        catch (CryptographicException ex)
        {
            throw new DpapiException($"DPAPI decryption failed: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            throw new DpapiException($"Unexpected DPAPI decryption error: {ex.Message}", ex);
        }
    }
}
