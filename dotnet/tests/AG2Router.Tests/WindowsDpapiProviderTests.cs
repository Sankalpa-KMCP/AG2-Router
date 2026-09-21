using System.Security.Cryptography;
using System.Text;
using AG2Router.Core.Models;
using AG2Router.Windows.Security;
using Xunit;

namespace AG2Router.Tests;

public class WindowsDpapiProviderTests
{
    [Fact]
    public async Task EncryptAndDecrypt_WithSyntheticBytes_RoundTripsAccurately()
    {
        if (!OperatingSystem.IsWindows()) return;

        var provider = new WindowsDpapiProvider();
        byte[] original = Encoding.UTF8.GetBytes("synthetic-test-payload-data-98765");
        byte[] copy = (byte[])original.Clone();

        byte[] encrypted = await provider.EncryptAsync(copy);
        Assert.NotNull(encrypted);
        Assert.NotEmpty(encrypted);
        Assert.NotEqual(original, encrypted);

        byte[] decrypted = await provider.DecryptAsync(encrypted);
        Assert.Equal(original, decrypted);
    }

    [Fact]
    public async Task Encrypt_WithNullOrEmpty_ThrowsDpapiException()
    {
        var provider = new WindowsDpapiProvider();

        await Assert.ThrowsAsync<DpapiException>(() => provider.EncryptAsync(null!));
        await Assert.ThrowsAsync<DpapiException>(() => provider.EncryptAsync(Array.Empty<byte>()));
    }

    [Fact]
    public async Task Decrypt_WithNullOrEmpty_ThrowsDpapiException()
    {
        var provider = new WindowsDpapiProvider();

        await Assert.ThrowsAsync<DpapiException>(() => provider.DecryptAsync(null!));
        await Assert.ThrowsAsync<DpapiException>(() => provider.DecryptAsync(Array.Empty<byte>()));
    }

    [Fact]
    public async Task Encrypt_ZeroesInputPlaintextBuffer_ForMemoryHygiene()
    {
        if (!OperatingSystem.IsWindows()) return;

        var provider = new WindowsDpapiProvider();
        byte[] plaintext = Encoding.UTF8.GetBytes("sensitive-test-secret-buffer");

        await provider.EncryptAsync(plaintext);

        // Verify that the original buffer was cleared with zeroes
        Assert.All(plaintext, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task Decrypt_WithTamperedCiphertext_ThrowsDpapiException()
    {
        if (!OperatingSystem.IsWindows()) return;

        var provider = new WindowsDpapiProvider();
        byte[] plaintext = Encoding.UTF8.GetBytes("synthetic-payload-to-tamper");
        byte[] encrypted = await provider.EncryptAsync(plaintext);

        // Corrupt/flip bits in ciphertext
        encrypted[^1] ^= 0xFF;
        encrypted[^2] ^= 0xAA;

        await Assert.ThrowsAsync<DpapiException>(() => provider.DecryptAsync(encrypted));
    }
}
