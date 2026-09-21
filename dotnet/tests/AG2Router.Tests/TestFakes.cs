using System.Collections.Concurrent;
using System.Security.Cryptography;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;

namespace AG2Router.Tests;

/// <summary>
/// In-memory fake Windows Credential Manager store for safe testing without OS mutation.
/// </summary>
public class InMemoryWinCredStore : IWinCredReader, IWinCredWriter
{
    private readonly ConcurrentDictionary<string, WinCredEntry> _store = new(StringComparer.OrdinalIgnoreCase);

    public Task<WinCredEntry?> ReadCredentialAsync(string target = "gemini:antigravity", CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new ArgumentException("Target cannot be empty", nameof(target));
        }

        if (_store.TryGetValue(target, out var entry))
        {
            var copy = new WinCredEntry(
                entry.Target,
                entry.Type,
                entry.UserName,
                entry.Persistence,
                (byte[])entry.Blob.Clone()
            );
            return Task.FromResult<WinCredEntry?>(copy);
        }

        return Task.FromResult<WinCredEntry?>(null);
    }

    public Task<bool> WriteCredentialAsync(WinCredEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (string.IsNullOrWhiteSpace(entry.Target))
        {
            throw new ArgumentException("Target cannot be empty");
        }

        if (entry.Type != 1)
        {
            throw new ArgumentException("Only Generic Credential (type 1) supported");
        }

        if (entry.Blob == null || entry.Blob.Length == 0)
        {
            throw new ArgumentException("Blob cannot be empty");
        }

        var copy = new WinCredEntry(
            entry.Target,
            entry.Type,
            entry.UserName,
            entry.Persistence,
            (byte[])entry.Blob.Clone()
        );

        _store[entry.Target] = copy;
        return Task.FromResult(true);
    }

    public void Seed(string target, string userName, byte[] blob)
    {
        _store[target] = new WinCredEntry(target, 1, userName, 2, (byte[])blob.Clone());
    }

    public void Clear() => _store.Clear();
}

/// <summary>
/// In-memory fake DPAPI provider using XOR transformation with signature header.
/// Fails closed on corruption/tampering to test DPAPI edge cases without Windows dependency.
/// </summary>
public class FakeDpapiProvider : IDpapiProvider
{
    private static readonly byte[] XorKey = "AG2_ROUTER_FAKE_DPAPI_KEY"u8.ToArray();
    private static readonly byte[] MagicHeader = [0xDE, 0xAD, 0xBE, 0xEF];

    public Task<byte[]> EncryptAsync(byte[] plaintext, CancellationToken cancellationToken = default)
    {
        if (plaintext == null || plaintext.Length == 0)
        {
            throw new DpapiException("Cannot encrypt empty or null payload");
        }

        var result = new byte[plaintext.Length + MagicHeader.Length];
        Array.Copy(MagicHeader, 0, result, 0, MagicHeader.Length);

        for (int i = 0; i < plaintext.Length; i++)
        {
            result[i + MagicHeader.Length] = (byte)(plaintext[i] ^ XorKey[i % XorKey.Length]);
        }

        // Memory hygiene
        CryptographicOperations.ZeroMemory(plaintext);

        return Task.FromResult(result);
    }

    public Task<byte[]> DecryptAsync(byte[] ciphertext, CancellationToken cancellationToken = default)
    {
        if (ciphertext == null || ciphertext.Length == 0)
        {
            throw new DpapiException("Cannot decrypt empty or null ciphertext");
        }

        if (ciphertext.Length < MagicHeader.Length)
        {
            throw new DpapiException("Ciphertext corrupted: payload too short");
        }

        for (int i = 0; i < MagicHeader.Length; i++)
        {
            if (ciphertext[i] != MagicHeader[i])
            {
                throw new DpapiException("Ciphertext corrupted or tampered: invalid signature header");
            }
        }

        var result = new byte[ciphertext.Length - MagicHeader.Length];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = (byte)(ciphertext[i + MagicHeader.Length] ^ XorKey[i % XorKey.Length]);
        }

        return Task.FromResult(result);
    }
}


