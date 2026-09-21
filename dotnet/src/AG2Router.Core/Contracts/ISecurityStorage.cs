namespace AG2Router.Core.Contracts;

/// <summary>
/// Representation of a Windows Credential entry.
/// </summary>
public record WinCredEntry(
    string Target,
    uint Type,
    string UserName,
    uint Persistence,
    byte[] Blob
);

/// <summary>
/// Strictly read-only access to Windows Credential Manager.
/// Excludes mutation functions by design.
/// </summary>
public interface IWinCredReader
{
    /// <summary>
    /// Reads a generic credential entry for the specified target (defaults to 'gemini:antigravity').
    /// Returns null if target does not exist.
    /// </summary>
    Task<WinCredEntry?> ReadCredentialAsync(string target = "gemini:antigravity", CancellationToken cancellationToken = default);
}

/// <summary>
/// Restricted writing to Windows Credential Manager.
/// </summary>
public interface IWinCredWriter
{
    /// <summary>
    /// Writes or updates a generic credential entry.
    /// </summary>
    Task<bool> WriteCredentialAsync(WinCredEntry entry, CancellationToken cancellationToken = default);
}

/// <summary>
/// Windows Data Protection API (DPAPI) contract.
/// Scoped to CurrentUser.
/// </summary>
public interface IDpapiProvider
{
    /// <summary>
    /// Encrypts plaintext bytes using DPAPI CurrentUser scope.
    /// </summary>
    Task<byte[]> EncryptAsync(byte[] plaintext, CancellationToken cancellationToken = default);

    /// <summary>
    /// Decrypts ciphertext bytes using DPAPI CurrentUser scope.
    /// Fails closed on tampering, corruption, or user mismatch.
    /// </summary>
    Task<byte[]> DecryptAsync(byte[] ciphertext, CancellationToken cancellationToken = default);
}
