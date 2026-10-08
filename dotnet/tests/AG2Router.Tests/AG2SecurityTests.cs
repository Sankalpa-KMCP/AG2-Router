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
    public void RedactSensitiveText_QuotedJsonFields_RedactsAllRecognizedSecrets()
    {
        var json = "{\"csrfToken\": \"SYNTHETIC_SECRET_A\", \"accessToken\": \"SYNTHETIC_SECRET_B\", \"refresh_token\": \"SYNTHETIC_SECRET_C\", \"apiKey\": \"SYNTHETIC_SECRET_D\", \"clientSecret\": \"SYNTHETIC_SECRET_E\"}";
        var redacted = AG2Security.RedactSensitiveText(json);

        Assert.DoesNotContain("SYNTHETIC_SECRET_A", redacted);
        Assert.DoesNotContain("SYNTHETIC_SECRET_B", redacted);
        Assert.DoesNotContain("SYNTHETIC_SECRET_C", redacted);
        Assert.DoesNotContain("SYNTHETIC_SECRET_D", redacted);
        Assert.DoesNotContain("SYNTHETIC_SECRET_E", redacted);
        Assert.Contains("\"csrfToken\": \"[REDACTED]\"", redacted);
        Assert.Contains("\"accessToken\": \"[REDACTED]\"", redacted);
        Assert.Contains("\"refresh_token\": \"[REDACTED]\"", redacted);
        Assert.Contains("\"apiKey\": \"[REDACTED]\"", redacted);
        Assert.Contains("\"clientSecret\": \"[REDACTED]\"", redacted);
    }

    [Fact]
    public void RedactSensitiveText_NestedJson_RedactsNestedSecretProperties()
    {
        var json = "{\"error\": {\"code\": 403, \"details\": {\"sessionToken\": \"SYNTHETIC_NESTED_SECRET\"}}}";
        var redacted = AG2Security.RedactSensitiveText(json);

        Assert.DoesNotContain("SYNTHETIC_NESTED_SECRET", redacted);
        Assert.Contains("\"sessionToken\": \"[REDACTED]\"", redacted);
        Assert.Contains("\"code\": 403", redacted);
    }

    [Fact]
    public void RedactSensitiveText_SecretFieldsInsideArrays_RedactsEachElement()
    {
        var json = "{\"tokens\": [{\"accessToken\": \"SYNTHETIC_ARRAY_1\"}, {\"refreshToken\": \"SYNTHETIC_ARRAY_2\"}]}";
        var redacted = AG2Security.RedactSensitiveText(json);

        Assert.DoesNotContain("SYNTHETIC_ARRAY_1", redacted);
        Assert.DoesNotContain("SYNTHETIC_ARRAY_2", redacted);
        Assert.Contains("\"accessToken\": \"[REDACTED]\"", redacted);
        Assert.Contains("\"refreshToken\": \"[REDACTED]\"", redacted);
    }

    [Fact]
    public void RedactSensitiveText_EscapedStringContents_PreservesStructureWithoutBreakingQuotes()
    {
        var json = "{\"csrfToken\": \"SYNTHETIC_\\\"ESCAPED\\\\_SECRET\", \"status\": \"failed\"}";
        var redacted = AG2Security.RedactSensitiveText(json);

        Assert.DoesNotContain("SYNTHETIC_", redacted);
        Assert.Contains("\"csrfToken\": \"[REDACTED]\"", redacted);
        Assert.Contains("\"status\": \"failed\"", redacted);
    }

    [Fact]
    public void RedactSensitiveText_MixedJsonAndOrdinaryText_PreservesSurroundingText()
    {
        var text = "Daemon error at 127.0.0.1:4000: {\"csrfToken\": \"SYNTHETIC_MIXED_SECRET\", \"status\": \"error\"} - retry later";
        var redacted = AG2Security.RedactSensitiveText(text);

        Assert.DoesNotContain("SYNTHETIC_MIXED_SECRET", redacted);
        Assert.Contains("\"csrfToken\": \"[REDACTED]\"", redacted);
        Assert.StartsWith("Daemon error at 127.0.0.1:4000: ", redacted);
        Assert.EndsWith(" - retry later", redacted);
    }

    [Fact]
    public void RedactSensitiveText_MalformedOrTruncatedJson_RedactsSafely()
    {
        var truncatedQuoted = "{\"csrfToken\": \"SYNTHETIC_TRUNCATED_SECRET";
        var redacted1 = AG2Security.RedactSensitiveText(truncatedQuoted);

        Assert.DoesNotContain("SYNTHETIC_TRUNCATED_SECRET", redacted1);
        Assert.Contains("\"csrfToken\": \"[REDACTED]\"", redacted1);

        var truncatedUnquoted = "{\"csrfToken\": SYNTHETIC_UNQUOTED_SECRET";
        var redacted2 = AG2Security.RedactSensitiveText(truncatedUnquoted);

        Assert.DoesNotContain("SYNTHETIC_UNQUOTED_SECRET", redacted2);
        Assert.Contains("\"csrfToken\": [REDACTED]", redacted2);
    }

    [Fact]
    public void RedactSensitiveText_CasingVariants_RedactsRegardlessOfCase()
    {
        var json = "{\"CSRFTOKEN\": \"SYNTHETIC_CASE_1\", \"AccessToken\": \"SYNTHETIC_CASE_2\", \"REFRESH_TOKEN\": \"SYNTHETIC_CASE_3\"}";
        var redacted = AG2Security.RedactSensitiveText(json);

        Assert.DoesNotContain("SYNTHETIC_CASE_1", redacted);
        Assert.DoesNotContain("SYNTHETIC_CASE_2", redacted);
        Assert.DoesNotContain("SYNTHETIC_CASE_3", redacted);
        Assert.Contains("\"CSRFTOKEN\": \"[REDACTED]\"", redacted);
        Assert.Contains("\"AccessToken\": \"[REDACTED]\"", redacted);
        Assert.Contains("\"REFRESH_TOKEN\": \"[REDACTED]\"", redacted);
    }

    [Fact]
    public void RedactSensitiveText_LargeBoundedInput_ExecutesLinearlyWithoutBacktracking()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 500; i++)
        {
            sb.Append($"{{\"index\": {i}, \"status\": \"ok\", \"csrfToken\": \"SYNTHETIC_LARGE_{i}\"}}\n");
        }
        var largeText = sb.ToString();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var redacted = AG2Security.RedactSensitiveText(largeText);
        sw.Stop();

        Assert.DoesNotContain("SYNTHETIC_LARGE_", redacted);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"Redaction took too long: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void RedactSensitiveText_NonsensitiveProperties_AreNotFalselyRedacted()
    {
        var json = "{\"status\": \"ok\", \"tokenCount\": 42, \"keyboard\": \"usb\", \"keyId\": 99, \"username\": \"admin\"}";
        var redacted = AG2Security.RedactSensitiveText(json);

        Assert.Equal(json, redacted);
    }

    [Fact]
    public void SanitizeError_ScrubsEmbeddedTokensFromExceptionMessage()
    {
        var ex = new Exception("Error sending request with --csrf_token secret_token_xyz and {\"accessToken\": \"secret_json_abc\"}");
        var sanitized = AG2Security.SanitizeError(ex);

        Assert.DoesNotContain("secret_token_xyz", sanitized);
        Assert.DoesNotContain("secret_json_abc", sanitized);
        Assert.Contains("[REDACTED]", sanitized);
    }

    [Fact]
    public void F1_RedactSensitiveText_MixedQuotes_DoubleQuotedWithSingleQuote_DoesNotLeakSuffix()
    {
        var input = "{\"password\":\"SYN_PREFIX'SYN_SUFFIX\"}";
        var redacted = AG2Security.RedactSensitiveText(input);

        Assert.DoesNotContain("SYN_PREFIX", redacted);
        Assert.DoesNotContain("SYN_SUFFIX", redacted);
        Assert.DoesNotContain("'", redacted);
        Assert.Equal("{\"password\":\"[REDACTED]\"}", redacted);
    }

    [Fact]
    public void F1_RedactSensitiveText_MixedQuotes_SingleQuotedWithDoubleQuote_DoesNotLeakSuffix()
    {
        var input = "{'token':'SYN_PREFIX\"SYN_SUFFIX'}";
        var redacted = AG2Security.RedactSensitiveText(input);

        Assert.DoesNotContain("SYN_PREFIX", redacted);
        Assert.DoesNotContain("SYN_SUFFIX", redacted);
        Assert.DoesNotContain("\"", redacted);
        Assert.Equal("{'token':'[REDACTED]'}", redacted);
    }

    [Fact]
    public void F1_RedactSensitiveText_EscapedQuotesAndBackslashes_DoesNotBreakDelimiterRecognition()
    {
        var escapedQuotes = "{\"password\":\"SYN_PREFIX\\\"SYN_SUFFIX\"}";
        var redactedQuotes = AG2Security.RedactSensitiveText(escapedQuotes);
        Assert.DoesNotContain("SYN_PREFIX", redactedQuotes);
        Assert.DoesNotContain("SYN_SUFFIX", redactedQuotes);
        Assert.Equal("{\"password\":\"[REDACTED]\"}", redactedQuotes);

        var escapedBackslash = "{\"password\":\"SYN_PREFIX\\\\SYN_SUFFIX\"}";
        var redactedBackslash = AG2Security.RedactSensitiveText(escapedBackslash);
        Assert.DoesNotContain("SYN_PREFIX", redactedBackslash);
        Assert.DoesNotContain("SYN_SUFFIX", redactedBackslash);
        Assert.Equal("{\"password\":\"[REDACTED]\"}", redactedBackslash);

        var backslashQuote = "{\"password\":\"SYN_PREFIX\\\\\\\"SYN_SUFFIX\"}";
        var redactedBackslashQuote = AG2Security.RedactSensitiveText(backslashQuote);
        Assert.DoesNotContain("SYN_PREFIX", redactedBackslashQuote);
        Assert.DoesNotContain("SYN_SUFFIX", redactedBackslashQuote);
        Assert.Equal("{\"password\":\"[REDACTED]\"}", redactedBackslashQuote);
    }

    [Fact]
    public void F1_RedactSensitiveText_MalformedRawNewlines_DoesNotPrematurelyTerminateOrLeakSuffix()
    {
        var lf = "{\"password\":\"SYN_PREFIX\nSYN_SUFFIX\"}";
        var redactedLf = AG2Security.RedactSensitiveText(lf);
        Assert.DoesNotContain("SYN_PREFIX", redactedLf);
        Assert.DoesNotContain("SYN_SUFFIX", redactedLf);
        Assert.Equal("{\"password\":\"[REDACTED]\"}", redactedLf);

        var crlf = "{\"password\":\"SYN_PREFIX\r\nSYN_SUFFIX\"}";
        var redactedCrlf = AG2Security.RedactSensitiveText(crlf);
        Assert.DoesNotContain("SYN_PREFIX", redactedCrlf);
        Assert.DoesNotContain("SYN_SUFFIX", redactedCrlf);
        Assert.Equal("{\"password\":\"[REDACTED]\"}", redactedCrlf);

        var truncatedWithLf = "{\"password\":\"SYN_PREFIX\nSYN_SUFFIX";
        var redactedTruncated = AG2Security.RedactSensitiveText(truncatedWithLf);
        Assert.DoesNotContain("SYN_PREFIX", redactedTruncated);
        Assert.DoesNotContain("SYN_SUFFIX", redactedTruncated);
        Assert.Equal("{\"password\":\"[REDACTED]\"", redactedTruncated);
    }

    [Fact]
    public void F2_RedactSensitiveText_JsonAuthorization_RedactsRegardlessOfScheme()
    {
        var basicJson = "{\"authorization\":\"Basic SYN_BASIC_TOKEN\"}";
        var redactedBasic = AG2Security.RedactSensitiveText(basicJson);
        Assert.DoesNotContain("SYN_BASIC_TOKEN", redactedBasic);
        Assert.Equal("{\"authorization\":\"[REDACTED]\"}", redactedBasic);

        var bearerJson = "{\"authorization\":\"Bearer SYN_BEARER_TOKEN\"}";
        var redactedBearer = AG2Security.RedactSensitiveText(bearerJson);
        Assert.DoesNotContain("SYN_BEARER_TOKEN", redactedBearer);
        Assert.Equal("{\"authorization\":\"[REDACTED]\"}", redactedBearer);

        var customJson = "{\"authorization\":\"Custom SYN_CUSTOM_TOKEN\"}";
        var redactedCustom = AG2Security.RedactSensitiveText(customJson);
        Assert.DoesNotContain("SYN_CUSTOM_TOKEN", redactedCustom);
        Assert.Equal("{\"authorization\":\"[REDACTED]\"}", redactedCustom);

        var digestJson = "{\"authorization\":\"Digest SYN_DIGEST_TOKEN\"}";
        var redactedDigest = AG2Security.RedactSensitiveText(digestJson);
        Assert.DoesNotContain("SYN_DIGEST_TOKEN", redactedDigest);
        Assert.Equal("{\"authorization\":\"[REDACTED]\"}", redactedDigest);

        var singleQuoteJson = "{'authorization':'Basic SYN_SINGLE_BASIC'}";
        var redactedSingle = AG2Security.RedactSensitiveText(singleQuoteJson);
        Assert.DoesNotContain("SYN_SINGLE_BASIC", redactedSingle);
        Assert.Equal("{'authorization':'[REDACTED]'}", redactedSingle);
    }

    [Fact]
    public void F2_RedactSensitiveText_HttpHeaderAuthorization_RedactsRegardlessOfScheme()
    {
        var basicHeader = "Authorization: Basic SYN_BASIC_HEADER\r\n";
        var redactedBasic = AG2Security.RedactSensitiveText(basicHeader);
        Assert.DoesNotContain("SYN_BASIC_HEADER", redactedBasic);
        Assert.Equal("Authorization: [REDACTED]\r\n", redactedBasic);

        var bearerHeader = "Authorization: Bearer SYN_BEARER_HEADER\r\n";
        var redactedBearer = AG2Security.RedactSensitiveText(bearerHeader);
        Assert.DoesNotContain("SYN_BEARER_HEADER", redactedBearer);
        Assert.Equal("Authorization: [REDACTED]\r\n", redactedBearer);

        var customHeader = "Authorization: Custom SYN_CUSTOM_HEADER\r\n";
        var redactedCustom = AG2Security.RedactSensitiveText(customHeader);
        Assert.DoesNotContain("SYN_CUSTOM_HEADER", redactedCustom);
        Assert.Equal("Authorization: [REDACTED]\r\n", redactedCustom);

        var casingHeader = "AUTHORIZATION: Digest SYN_DIGEST_HEADER\r\n";
        var redactedCasing = AG2Security.RedactSensitiveText(casingHeader);
        Assert.DoesNotContain("SYN_DIGEST_HEADER", redactedCasing);
        Assert.Equal("AUTHORIZATION: [REDACTED]\r\n", redactedCasing);
    }

    [Fact]
    public void R1_Reproduction_WhitespaceAfterColon_RedactsSecretSafely()
    {
        var input = "{\"password\":\n  \"SYN_RPC_WHITESPACE_SECRET\",\"status\":\"failed\"}";
        var redacted = AG2Security.RedactSensitiveText(input);

        Assert.DoesNotContain("SYN_RPC_WHITESPACE_SECRET", redacted);
        Assert.Contains("\"password\":\n  \"[REDACTED]\"", redacted);
        Assert.Contains("\"status\":\"failed\"", redacted);
    }

    [Fact]
    public void R1_Reproduction_WhitespaceBeforeColon_RedactsSecretSafely()
    {
        var input = "{\"password\"\n: \"SYN_RPC_LF_BEFORE_COLON\",\"status\":\"failed\"}";
        var redacted = AG2Security.RedactSensitiveText(input);

        Assert.DoesNotContain("SYN_RPC_LF_BEFORE_COLON", redacted);
        Assert.Contains("\"password\"\n: \"[REDACTED]\"", redacted);
        Assert.Contains("\"status\":\"failed\"", redacted);
    }

    [Fact]
    public void R2_Reproduction_DigestAuthorizationWithCommas_RedactsAllParameters()
    {
        var input = "Authorization: Digest username=\"SYN_RPC_USER\", response=\"SYN_RPC_DIGEST_RESPONSE\", nonce=\"SYN_RPC_NONCE\"\r\nX-Trace: ok";
        var redacted = AG2Security.RedactSensitiveText(input);

        Assert.DoesNotContain("SYN_RPC_USER", redacted);
        Assert.DoesNotContain("SYN_RPC_DIGEST_RESPONSE", redacted);
        Assert.DoesNotContain("SYN_RPC_NONCE", redacted);
        Assert.Equal("Authorization: [REDACTED]\r\nX-Trace: ok", redacted);
    }

    [Fact]
    public void R1_PermanentMatrix_CoveringAllWhitespaceLayouts()
    {
        // A. LF before JSON colon
        var a = "{\"password\"\n: \"SYN_SECRET_A\"}";
        var redA = AG2Security.RedactSensitiveText(a);
        Assert.DoesNotContain("SYN_SECRET_A", redA);
        Assert.Equal("{\"password\"\n: \"[REDACTED]\"}", redA);

        // B. LF after JSON colon
        var b = "{\"password\":\n \"SYN_SECRET_B\"}";
        var redB = AG2Security.RedactSensitiveText(b);
        Assert.DoesNotContain("SYN_SECRET_B", redB);
        Assert.Equal("{\"password\":\n \"[REDACTED]\"}", redB);

        // C. CRLF before and after JSON colon
        var c = "{\"password\"\r\n:\r\n\"SYN_SECRET_C\"}";
        var redC = AG2Security.RedactSensitiveText(c);
        Assert.DoesNotContain("SYN_SECRET_C", redC);
        Assert.Equal("{\"password\"\r\n:\r\n\"[REDACTED]\"}", redC);

        // D. TAB and SPACE combinations
        var d = "{\"password\" \t \n : \t \r\n \t \"SYN_SECRET_D\"}";
        var redD = AG2Security.RedactSensitiveText(d);
        Assert.DoesNotContain("SYN_SECRET_D", redD);
        Assert.Equal("{\"password\" \t \n : \t \r\n \t \"[REDACTED]\"}", redD);

        // E. Multiple adjacent sensitive JSON properties with different whitespace layouts
        var e = "{\"password\":\n\"SYN_SECRET_E1\", \"apiKey\"\r\n:\r\n \"SYN_SECRET_E2\"}";
        var redE = AG2Security.RedactSensitiveText(e);
        Assert.DoesNotContain("SYN_SECRET_E1", redE);
        Assert.DoesNotContain("SYN_SECRET_E2", redE);
        Assert.Contains("\"password\":\n\"[REDACTED]\"", redE);
        Assert.Contains("\"apiKey\"\r\n:\r\n \"[REDACTED]\"", redE);

        // F. Nested objects containing multiline sensitive fields
        var f = "{\"user\": {\"password\":\n \"SYN_SECRET_F\"}}";
        var redF = AG2Security.RedactSensitiveText(f);
        Assert.DoesNotContain("SYN_SECRET_F", redF);
        Assert.Contains("\"password\":\n \"[REDACTED]\"", redF);

        // G. Arrays of objects containing multiline sensitive fields
        var g = "[{\"password\":\n\"SYN_SECRET_G1\"}, {\"token\"\n:\n\"SYN_SECRET_G2\"}]";
        var redG = AG2Security.RedactSensitiveText(g);
        Assert.DoesNotContain("SYN_SECRET_G1", redG);
        Assert.DoesNotContain("SYN_SECRET_G2", redG);
        Assert.Contains("\"password\":\n\"[REDACTED]\"", redG);
        Assert.Contains("\"token\"\n:\n\"[REDACTED]\"", redG);

        // H. A recognized multiline authorization JSON property
        var h = "{\"authorization\":\n  \"Basic SYN_SECRET_H\"}";
        var redH = AG2Security.RedactSensitiveText(h);
        Assert.DoesNotContain("SYN_SECRET_H", redH);
        Assert.Equal("{\"authorization\":\n  \"[REDACTED]\"}", redH);

        // I. Nonsensitive properties following a redacted multiline secret remain intact
        var i = "{\"password\":\n\"SYN_SECRET_I\", \"status\": \"ok\", \"code\": 200}";
        var redI = AG2Security.RedactSensitiveText(i);
        Assert.DoesNotContain("SYN_SECRET_I", redI);
        Assert.Contains("\"password\":\n\"[REDACTED]\"", redI);
        Assert.Contains("\"status\": \"ok\"", redI);
        Assert.Contains("\"code\": 200", redI);

        // J. An unrelated identifier such as tokenCount remains readable
        var j = "{\"tokenCount\": 42, \"keyboard\": \"usb\", \"keyId\": 99}";
        var redJ = AG2Security.RedactSensitiveText(j);
        Assert.Equal(j, redJ);
    }

    [Fact]
    public void R2_PermanentMatrix_CoveringAuthorizationHeaderBoundaries()
    {
        // A. Basic Authorization header
        var a = "Authorization: Basic SYN_BASIC_A\r\n";
        var redA = AG2Security.RedactSensitiveText(a);
        Assert.DoesNotContain("SYN_BASIC_A", redA);
        Assert.Equal("Authorization: [REDACTED]\r\n", redA);

        // B. Bearer Authorization header
        var b = "Authorization: Bearer SYN_BEARER_B\r\n";
        var redB = AG2Security.RedactSensitiveText(b);
        Assert.DoesNotContain("SYN_BEARER_B", redB);
        Assert.Equal("Authorization: [REDACTED]\r\n", redB);

        // C. Digest with comma-separated parameters
        var c = "Authorization: Digest username=\"SYN_USER_C\", response=\"SYN_RESP_C\", nonce=\"SYN_NONCE_C\"\r\n";
        var redC = AG2Security.RedactSensitiveText(c);
        Assert.DoesNotContain("SYN_USER_C", redC);
        Assert.DoesNotContain("SYN_RESP_C", redC);
        Assert.DoesNotContain("SYN_NONCE_C", redC);
        Assert.Equal("Authorization: [REDACTED]\r\n", redC);

        // D. Digest with semicolon-containing parameter content
        var d = "Authorization: Digest username=\"SYN_USER_D\"; response=\"SYN_RESP_D\"; nonce=\"SYN_NONCE_D\"\r\n";
        var redD = AG2Security.RedactSensitiveText(d);
        Assert.DoesNotContain("SYN_USER_D", redD);
        Assert.DoesNotContain("SYN_RESP_D", redD);
        Assert.DoesNotContain("SYN_NONCE_D", redD);
        Assert.Equal("Authorization: [REDACTED]\r\n", redD);

        // E. Quoted Digest parameter values
        var e = "Authorization: Digest realm=\"users@example.com\", nonce=\"dcd98b7102dd2f0e8b11d0f600bfb0c093\", uri=\"/dir/index.html\", qop=auth, nc=00000001, cnonce=\"0a4f113b\", response=\"6629fae49393a05397450978507c4ef1\", opaque=\"5ccc069c403ebaf9f0171e9517f40e41\"\r\n";
        var redE = AG2Security.RedactSensitiveText(e);
        Assert.DoesNotContain("dcd98b7102dd2f0e8b11d0f600bfb0c093", redE);
        Assert.DoesNotContain("6629fae49393a05397450978507c4ef1", redE);
        Assert.Equal("Authorization: [REDACTED]\r\n", redE);

        // F. Authorization with plus, slash and equals characters
        var f = "Authorization: Basic dXNlcjpwYXNzKzEvPT0=\r\n";
        var redF = AG2Security.RedactSensitiveText(f);
        Assert.DoesNotContain("dXNlcjpwYXNzKzEvPT0=", redF);
        Assert.Equal("Authorization: [REDACTED]\r\n", redF);

        // G. Lowercase and mixed-case Authorization header
        var g1 = "authorization: Digest username=\"SYN_USER_G\", response=\"SYN_RESP_G\"\r\n";
        var redG1 = AG2Security.RedactSensitiveText(g1);
        Assert.DoesNotContain("SYN_USER_G", redG1);
        Assert.DoesNotContain("SYN_RESP_G", redG1);
        Assert.Equal("authorization: [REDACTED]\r\n", redG1);

        var g2 = "AUTHORIZATION: Digest username=\"SYN_USER_G\", response=\"SYN_RESP_G\"\r\n";
        var redG2 = AG2Security.RedactSensitiveText(g2);
        Assert.DoesNotContain("SYN_USER_G", redG2);
        Assert.DoesNotContain("SYN_RESP_G", redG2);
        Assert.Equal("AUTHORIZATION: [REDACTED]\r\n", redG2);

        // H. CRLF-followed nonsensitive header
        var h = "Authorization: Digest username=\"SYN_USER_H\", response=\"SYN_RESP_H\"\r\nX-Trace: ok_h\r\n";
        var redH = AG2Security.RedactSensitiveText(h);
        Assert.DoesNotContain("SYN_USER_H", redH);
        Assert.DoesNotContain("SYN_RESP_H", redH);
        Assert.Equal("Authorization: [REDACTED]\r\nX-Trace: ok_h\r\n", redH);

        // I. LF-followed nonsensitive header
        var i = "Authorization: Digest username=\"SYN_USER_I\", response=\"SYN_RESP_I\"\nX-Trace: ok_i\n";
        var redI = AG2Security.RedactSensitiveText(i);
        Assert.DoesNotContain("SYN_USER_I", redI);
        Assert.DoesNotContain("SYN_RESP_I", redI);
        Assert.Equal("Authorization: [REDACTED]\nX-Trace: ok_i\n", redI);

        // J. JSON authorization next to another JSON property
        var j = "{\"authorization\":\"Basic SYN_AUTH_J\",\"status\":\"failed\"}";
        var redJ = AG2Security.RedactSensitiveText(j);
        Assert.DoesNotContain("SYN_AUTH_J", redJ);
        Assert.Equal("{\"authorization\":\"[REDACTED]\",\"status\":\"failed\"}", redJ);

        // K. Multiline JSON authorization separator whitespace
        var k = "{\"authorization\":\n  \"Digest username=\\\"SYN_USER_K\\\", response=\\\"SYN_RESP_K\\\"\", \"status\": \"failed\"}";
        var redK = AG2Security.RedactSensitiveText(k);
        Assert.DoesNotContain("SYN_USER_K", redK);
        Assert.DoesNotContain("SYN_RESP_K", redK);
        Assert.Contains("\"authorization\":\n  \"[REDACTED]\"", redK);
        Assert.Contains("\"status\": \"failed\"", redK);
    }

    [Fact]
    public void F1_Reproduction_DigestRealmWithBracketOrBrace_LeaksTrailingCredentials_WhenPrematurelyTerminated()
    {
        // Reproduction case 1: realm containing bracket ']'
        var bracketHeader = "Authorization: Digest username=\"SYN_RPC_USER\", realm=\"service[team]\", nonce=\"SYN_RPC_NONCE\", response=\"SYN_RPC_RESPONSE\"\r\nX-Trace: ok\r\n";
        var redactedBracket = AG2Security.RedactSensitiveText(bracketHeader);

        Assert.DoesNotContain("SYN_RPC_USER", redactedBracket);
        Assert.DoesNotContain("SYN_RPC_NONCE", redactedBracket);
        Assert.DoesNotContain("SYN_RPC_RESPONSE", redactedBracket);
        Assert.Equal("Authorization: [REDACTED]\r\nX-Trace: ok\r\n", redactedBracket);

        // Reproduction case 2: realm containing brace '}'
        var braceHeader = "Authorization: Digest username=\"SYN_RPC_USER\", realm=\"service{team}\", nonce=\"SYN_RPC_NONCE\", response=\"SYN_RPC_RESPONSE\"\r\nX-Trace: ok\r\n";
        var redactedBrace = AG2Security.RedactSensitiveText(braceHeader);

        Assert.DoesNotContain("SYN_RPC_USER", redactedBrace);
        Assert.DoesNotContain("SYN_RPC_NONCE", redactedBrace);
        Assert.DoesNotContain("SYN_RPC_RESPONSE", redactedBrace);
        Assert.Equal("Authorization: [REDACTED]\r\nX-Trace: ok\r\n", redactedBrace);
    }

    [Fact]
    public void F1_PermanentMatrix_CoveringAuthorizationHeaderBoundaries()
    {
        // A: Digest realm containing ']'
        var a = "Authorization: Digest username=\"SYN_USER_A\", realm=\"service[team]\", nonce=\"SYN_NONCE_A\", response=\"SYN_RESP_A\"\r\n";
        var redA = AG2Security.RedactSensitiveText(a);
        Assert.DoesNotContain("SYN_USER_A", redA);
        Assert.DoesNotContain("SYN_NONCE_A", redA);
        Assert.DoesNotContain("SYN_RESP_A", redA);
        Assert.Equal("Authorization: [REDACTED]\r\n", redA);

        // B: Digest realm containing '}'
        var b = "Authorization: Digest username=\"SYN_USER_B\", realm=\"service{team}\", nonce=\"SYN_NONCE_B\", response=\"SYN_RESP_B\"\r\n";
        var redB = AG2Security.RedactSensitiveText(b);
        Assert.DoesNotContain("SYN_USER_B", redB);
        Assert.DoesNotContain("SYN_NONCE_B", redB);
        Assert.DoesNotContain("SYN_RESP_B", redB);
        Assert.Equal("Authorization: [REDACTED]\r\n", redB);

        // C: Digest realm containing both '[' and ']'
        var c = "Authorization: Digest realm=\"corp[prod][us-east]\", nonce=\"SYN_NONCE_C\", response=\"SYN_RESP_C\"\r\n";
        var redC = AG2Security.RedactSensitiveText(c);
        Assert.DoesNotContain("SYN_NONCE_C", redC);
        Assert.DoesNotContain("SYN_RESP_C", redC);
        Assert.Equal("Authorization: [REDACTED]\r\n", redC);

        // D: Digest realm containing both '{' and '}'
        var d = "Authorization: Digest realm=\"corp{prod}{us-east}\", nonce=\"SYN_NONCE_D\", response=\"SYN_RESP_D\"\r\n";
        var redD = AG2Security.RedactSensitiveText(d);
        Assert.DoesNotContain("SYN_NONCE_D", redD);
        Assert.DoesNotContain("SYN_RESP_D", redD);
        Assert.Equal("Authorization: [REDACTED]\r\n", redD);

        // E: Quoted parameter containing punctuation (commas, semicolons)
        var e = "Authorization: Digest realm=\"users,corp;dept=eng\", nonce=\"SYN_NONCE_E\", response=\"SYN_RESP_E\"\r\n";
        var redE = AG2Security.RedactSensitiveText(e);
        Assert.DoesNotContain("SYN_NONCE_E", redE);
        Assert.DoesNotContain("SYN_RESP_E", redE);
        Assert.Equal("Authorization: [REDACTED]\r\n", redE);

        // F: Multiple parameters following bracketed realm
        var f = "Authorization: Digest realm=\"service[alpha]\", qop=auth, nc=00000001, cnonce=\"SYN_CNONCE_F\", response=\"SYN_RESP_F\", opaque=\"SYN_OPAQUE_F\"\r\n";
        var redF = AG2Security.RedactSensitiveText(f);
        Assert.DoesNotContain("SYN_CNONCE_F", redF);
        Assert.DoesNotContain("SYN_RESP_F", redF);
        Assert.DoesNotContain("SYN_OPAQUE_F", redF);
        Assert.Equal("Authorization: [REDACTED]\r\n", redF);

        // G: Basic authorization with Base64 characters ('+', '/', '=')
        var g = "Authorization: Basic dXNlcm5hbWU6cGFzc3dvcmQrLz09\r\n";
        var redG = AG2Security.RedactSensitiveText(g);
        Assert.DoesNotContain("dXNlcm5hbWU6cGFzc3dvcmQrLz09", redG);
        Assert.Equal("Authorization: [REDACTED]\r\n", redG);

        // H: Bearer authorization
        var h = "Authorization: Bearer SYN_JWT_BEARER_TOKEN_H\r\n";
        var redH = AG2Security.RedactSensitiveText(h);
        Assert.DoesNotContain("SYN_JWT_BEARER_TOKEN_H", redH);
        Assert.Equal("Authorization: [REDACTED]\r\n", redH);

        // I: Custom authorization scheme
        var i = "Authorization: CustomScheme token_id=\"SYN_CUSTOM_I\", signature=\"SYN_SIG_I\"\r\n";
        var redI = AG2Security.RedactSensitiveText(i);
        Assert.DoesNotContain("SYN_CUSTOM_I", redI);
        Assert.DoesNotContain("SYN_SIG_I", redI);
        Assert.Equal("Authorization: [REDACTED]\r\n", redI);

        // J: CRLF-delimited following header
        var j = "Authorization: Digest username=\"SYN_USER_J\", realm=\"service[team]\"\r\nX-Custom-Header: value_j\r\n";
        var redJ = AG2Security.RedactSensitiveText(j);
        Assert.DoesNotContain("SYN_USER_J", redJ);
        Assert.Equal("Authorization: [REDACTED]\r\nX-Custom-Header: value_j\r\n", redJ);

        // K: LF-delimited following header
        var k = "Authorization: Digest username=\"SYN_USER_K\", realm=\"service[team]\"\nX-Custom-Header: value_k\n";
        var redK = AG2Security.RedactSensitiveText(k);
        Assert.DoesNotContain("SYN_USER_K", redK);
        Assert.Equal("Authorization: [REDACTED]\nX-Custom-Header: value_k\n", redK);

        // L: CR-delimited following header
        var l = "Authorization: Digest username=\"SYN_USER_L\", realm=\"service[team]\"\rX-Custom-Header: value_l\r";
        var redL = AG2Security.RedactSensitiveText(l);
        Assert.DoesNotContain("SYN_USER_L", redL);
        Assert.Equal("Authorization: [REDACTED]\rX-Custom-Header: value_l\r", redL);

        // M: Authorization ending at EOF
        var m = "Authorization: Digest username=\"SYN_USER_M\", realm=\"service[team]\", nonce=\"SYN_NONCE_M\"";
        var redM = AG2Security.RedactSensitiveText(m);
        Assert.DoesNotContain("SYN_USER_M", redM);
        Assert.DoesNotContain("SYN_NONCE_M", redM);
        Assert.Equal("Authorization: [REDACTED]", redM);

        // N: Lowercase and mixed-case header names
        var n1 = "authorization: Digest realm=\"service[team]\", nonce=\"SYN_NONCE_N1\"\r\n";
        var redN1 = AG2Security.RedactSensitiveText(n1);
        Assert.DoesNotContain("SYN_NONCE_N1", redN1);
        Assert.Equal("authorization: [REDACTED]\r\n", redN1);

        var n2 = "AUTHORIZATION: Digest realm=\"service{team}\", nonce=\"SYN_NONCE_N2\"\r\n";
        var redN2 = AG2Security.RedactSensitiveText(n2);
        Assert.DoesNotContain("SYN_NONCE_N2", redN2);
        Assert.Equal("AUTHORIZATION: [REDACTED]\r\n", redN2);

        // O: Empty authorization value
        var o1 = "Authorization:\r\n";
        var redO1 = AG2Security.RedactSensitiveText(o1);
        Assert.Equal("Authorization:[REDACTED]\r\n", redO1);

        var o2 = "Authorization: \r\n";
        var redO2 = AG2Security.RedactSensitiveText(o2);
        Assert.Equal("Authorization: [REDACTED]\r\n", redO2);

        // P: Multiple quoted parameters
        var p = "Authorization: Digest uri=\"/rpc/v1\", realm=\"cluster[1]\", nonce=\"SYN_NONCE_P\", response=\"SYN_RESP_P\", algorithm=\"MD5\"\r\n";
        var redP = AG2Security.RedactSensitiveText(p);
        Assert.DoesNotContain("SYN_NONCE_P", redP);
        Assert.DoesNotContain("SYN_RESP_P", redP);
        Assert.Equal("Authorization: [REDACTED]\r\n", redP);

        // Q: Nonsensitive following header preserved
        var q = "Authorization: Digest realm=\"service[prod]\", nonce=\"SYN_NONCE_Q\"\r\nX-Request-Id: req-12345\r\nHost: 127.0.0.1:4000\r\n";
        var redQ = AG2Security.RedactSensitiveText(q);
        Assert.DoesNotContain("SYN_NONCE_Q", redQ);
        Assert.Equal("Authorization: [REDACTED]\r\nX-Request-Id: req-12345\r\nHost: 127.0.0.1:4000\r\n", redQ);
    }
}
