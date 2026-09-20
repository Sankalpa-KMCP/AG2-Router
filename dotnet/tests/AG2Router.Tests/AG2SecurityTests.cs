using AG2Router.AG2.Security;
using Xunit;

namespace AG2Router.Tests;

public class AG2SecurityTests
{
    [Fact]
    public void MaskToken_ShortOrEmpty_ReturnsRedactedOrEmpty()
    {
        Assert.Equal(string.Empty, AG2Security.MaskToken(null));
        Assert.Equal(string.Empty, AG2Security.MaskToken(""));
        Assert.Equal("[REDACTED]", AG2Security.MaskToken("12345678"));
        Assert.Equal("[REDACTED]", AG2Security.MaskToken("short"));
    }

    [Fact]
    public void MaskToken_LongToken_MasksMiddle()
    {
        var token = "11111111-2222-3333-4444-555555555555";
        var masked = AG2Security.MaskToken(token);
        Assert.Equal("1111...5555", masked);
    }

    [Fact]
    public void ExtractCsrfToken_ParsesBothSpaceAndEqualFormats()
    {
        var cmd1 = @"C:\bin\language_server.exe --standalone --csrf_token 11111111-2222-3333-4444-555555555555 --port 0";
        var cmd2 = @"C:\bin\language_server.exe --standalone --csrf_token=11111111-2222-3333-4444-555555555555 --port 0";

        Assert.Equal("11111111-2222-3333-4444-555555555555", AG2Security.ExtractCsrfToken(cmd1));
        Assert.Equal("11111111-2222-3333-4444-555555555555", AG2Security.ExtractCsrfToken(cmd2));
        Assert.Null(AG2Security.ExtractCsrfToken("no token here"));
    }

    [Fact]
    public void SanitizeCommandLine_RedactsAllSensitiveFlags()
    {
        var cmd = @"language_server.exe --csrf_token secret_csrf --host_bridge_token=secret_bridge --token secret_auth --password secret_pwd";
        var sanitized = AG2Security.SanitizeCommandLine(cmd);

        Assert.DoesNotContain("secret_csrf", sanitized);
        Assert.DoesNotContain("secret_bridge", sanitized);
        Assert.DoesNotContain("secret_auth", sanitized);
        Assert.DoesNotContain("secret_pwd", sanitized);
        Assert.Contains("[REDACTED]", sanitized);
    }

    [Fact]
    public void RedactSensitiveText_RedactsHeadersAndKeyValues()
    {
        var text = "x-codeium-csrf-token: secret123\nAuthorization: Bearer secret_bearer\ntoken: secret_token";
        var redacted = AG2Security.RedactSensitiveText(text);

        Assert.DoesNotContain("secret123", redacted);
        Assert.DoesNotContain("secret_bearer", redacted);
        Assert.DoesNotContain("secret_token", redacted);
        Assert.Contains("[REDACTED]", redacted);
    }

    [Fact]
    public void SanitizeError_ScrubsEmbeddedTokensFromExceptionMessage()
    {
        var ex = new Exception("Error sending request with --csrf_token secret_token_xyz");
        var sanitized = AG2Security.SanitizeError(ex);

        Assert.DoesNotContain("secret_token_xyz", sanitized);
        Assert.Contains("[REDACTED]", sanitized);
    }
}
