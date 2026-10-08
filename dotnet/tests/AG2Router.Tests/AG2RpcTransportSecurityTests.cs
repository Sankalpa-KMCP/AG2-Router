using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AG2Router.AG2.Rpc;
using Xunit;

namespace AG2Router.Tests;

/// <summary>
/// Regression and security tests for B2 release blocker:
/// 1. Production token-bearing AG2 daemon transport must explicitly bypass all HTTP proxies (UseProxy = false).
/// 2. Redirect responses must be rejected from response headers before response bodies are consumed (ResponseHeadersRead).
/// 3. Token-bearing requests strictly confine sensitive x-codeium-csrf-token to the verified local daemon endpoint.
/// </summary>
public sealed class AG2RpcTransportSecurityTests : IClassFixture<RpcChildProcessFixture>
{
    private const string SyntheticCsrfToken = "synthetic_csrf_token_secret_123456789";
    private readonly RpcChildProcessFixture _childFixture;

    public AG2RpcTransportSecurityTests(RpcChildProcessFixture childFixture)
    {
        _childFixture = childFixture;
    }

    /// <summary>
    /// PART 20: Immediate low-level tripwire verifying the production default SocketsHttpHandler
    /// disables both automatic redirect following and proxy usage.
    /// </summary>
    [Fact]
    public void DefaultHandler_HasAllowAutoRedirectFalse_AndUseProxyFalse()
    {
        var handler = AG2RpcClient.CreateDefaultHandler();
        Assert.False(handler.AllowAutoRedirect, "AllowAutoRedirect must be false on production default handler");
        Assert.False(handler.UseProxy, "UseProxy must be false on production default handler to bypass all HTTP proxies");
    }

    /// <summary>
    /// PART 18: Response-body nonconsumption test:
    /// Daemon A responds with 307 redirect status and headers with Content-Length 5,000,000,
    /// but withholds the body bytes. The client must reject based on headers (ResponseHeadersRead)
    /// without waiting for body completion.
    /// </summary>
    [Fact]
    public async Task ResponseHeadersRead_RejectsRedirectImmediately_WithoutConsumingStalledBody()
    {
        await using var serverA = new TestHttpServer();

        // Server A sends redirect headers and flushes, but intentionally delays/withholds body bytes
        serverA.CustomResponseWriter = async (req, stream, ct) =>
        {
            var header = "HTTP/1.1 307 Temporary Redirect\r\nConnection: close\r\nLocation: http://127.0.0.1:9999/redirect\r\nContent-Length: 5000000\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
            await stream.FlushAsync(ct);
            // Delay sending the body: if client waited for body completion, it would block for > 2 seconds
            await Task.Delay(2500, ct);
        };

        using var client = new AG2RpcClient();

        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetUserStatusAsync(serverA.Port, "http", SyntheticCsrfToken));
        sw.Stop();

        // The rejection must happen almost immediately from headers (< 1500ms), long before the 2500ms body delay
        Assert.True(sw.ElapsedMilliseconds < 1500,
            $"Expected redirect rejection from headers without waiting for body, but elapsed {sw.ElapsedMilliseconds}ms");
        Assert.Equal(HttpStatusCode.TemporaryRedirect, ex.StatusCode);
        Assert.Contains("redirect", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// PART 2 & PART 3: Fresh-process reproduction of PROMPT #041 F1:
    /// Demonstrates that an unhardened client (UseProxy = true) under an active proxy environment
    /// (HTTP_PROXY) forwards the request and sensitive CSRF token to the proxy listener instead of daemon A.
    /// </summary>
    [Fact]
    public async Task F1_Reproduction_UnhardenedProxyClient_ForwardsTokenToProxy_InFreshProcess()
    {
        await using var daemonA = new TestHttpServer();
        await using var proxyP = new TestHttpServer();

        daemonA.ResponseFactory = _ => (200, null, "application/json", "{\"userStatus\":{\"email\":\"daemon@example.com\"}}");
        proxyP.ResponseFactory = _ => (200, null, "application/json", "{\"userStatus\":{\"email\":\"proxy@example.com\"}}");

        // Run fresh child process with unhardened client (--unhardened-proxy) and proxy environment
        var result = await _childFixture.RunChildProcessAsync(
            targetPort: daemonA.Port,
            protocol: "http",
            token: SyntheticCsrfToken,
            mode: "rpc",
            proxyPort: proxyP.Port,
            unhardenedProxy: true);

        // Pre-fix unhardened observation:
        // Daemon A was NOT contacted (0 requests)
        Assert.Empty(daemonA.RecordedRequests);

        // Proxy P intercepted the request AND received the sensitive CSRF token!
        Assert.Single(proxyP.RecordedRequests);
        var proxyReq = proxyP.RecordedRequests[0];
        Assert.Equal("POST", proxyReq.Method);
        Assert.True(proxyReq.Headers.ContainsKey("x-codeium-csrf-token"), "Proxy intercepted the token header");
        Assert.Equal(SyntheticCsrfToken, proxyReq.Headers["x-codeium-csrf-token"]);
        Assert.StartsWith("RPC:OK:proxy@example.com", result);
    }

    /// <summary>
    /// PART 15: Production RPC proxy regression (Fresh Process):
    /// Production default client (UseProxy = false) in a fresh child process with configured
    /// proxy environment strictly bypasses the proxy and contacts daemon A directly.
    /// </summary>
    [Fact]
    public async Task ProductionDefaultClient_BypassesConfiguredProxy_ContactsDaemonDirectly_InFreshProcess()
    {
        await using var daemonA = new TestHttpServer();
        await using var proxyP = new TestHttpServer();

        daemonA.ResponseFactory = _ => (200, null, "application/json", "{\"userStatus\":{\"email\":\"direct-daemon@example.com\"}}");
        proxyP.ResponseFactory = _ => (200, null, "application/json", "{\"userStatus\":{\"email\":\"proxy@example.com\"}}");

        var result = await _childFixture.RunChildProcessAsync(
            targetPort: daemonA.Port,
            protocol: "http",
            token: SyntheticCsrfToken,
            mode: "rpc",
            proxyPort: proxyP.Port,
            unhardenedProxy: false);

        // Post-fix required invariant:
        // Proxy P receives ZERO requests and ZERO tokens
        Assert.Empty(proxyP.RecordedRequests);

        // Daemon A receives exactly 1 request directly
        Assert.Single(daemonA.RecordedRequests);
        var daemonReq = daemonA.RecordedRequests[0];
        Assert.Equal("POST", daemonReq.Method);
        Assert.Equal(SyntheticCsrfToken, daemonReq.Headers["x-codeium-csrf-token"]);

        Assert.Equal("RPC:OK:direct-daemon@example.com", result);
    }

    /// <summary>
    /// PART 14: Environment variable matrix across HTTP_PROXY, http_proxy, ALL_PROXY.
    /// Verifies all casings bypass proxying in fresh processes.
    /// </summary>
    [Theory]
    [InlineData("HTTP_PROXY")]
    [InlineData("http_proxy")]
    [InlineData("ALL_PROXY")]
    [InlineData("all_proxy")]
    public async Task ProxyEnvironmentMatrix_VariousVariableCasings_AllBypassProxy_InFreshProcess(string envVarName)
    {
        await using var daemonA = new TestHttpServer();
        await using var proxyP = new TestHttpServer();

        daemonA.ResponseFactory = _ => (200, null, "application/json", "{\"userStatus\":{\"email\":\"verified@example.com\"}}");
        proxyP.ResponseFactory = _ => (200, null, "application/json", "{\"userStatus\":{\"email\":\"proxy@example.com\"}}");

        var result = await _childFixture.RunChildProcessAsync(
            targetPort: daemonA.Port,
            protocol: "http",
            token: SyntheticCsrfToken,
            mode: "rpc",
            proxyPort: proxyP.Port,
            unhardenedProxy: false,
            proxyEnvVar: envVarName,
            setBothCasings: false);

        Assert.Empty(proxyP.RecordedRequests);
        Assert.Single(daemonA.RecordedRequests);
        Assert.Equal("RPC:OK:verified@example.com", result);
    }

    /// <summary>
    /// PART 16: ProbePortAsync proxy regression when daemon is active (Fresh Process):
    /// Verifies probe contacts daemon A directly and bypasses configured proxy.
    /// </summary>
    [Fact]
    public async Task ProbeProxyRegression_WhenDaemonActive_ProbesDirectlyAndBypassesProxy_InFreshProcess()
    {
        await using var daemonA = new TestHttpServer();
        await using var proxyP = new TestHttpServer();

        daemonA.ResponseFactory = _ => (200, null, "application/json", "{}");
        proxyP.ResponseFactory = _ => (200, null, "application/json", "{}");

        var result = await _childFixture.RunChildProcessAsync(
            targetPort: daemonA.Port,
            protocol: "http",
            token: SyntheticCsrfToken,
            mode: "probe",
            proxyPort: proxyP.Port,
            unhardenedProxy: false);

        Assert.Empty(proxyP.RecordedRequests);
        Assert.Single(daemonA.RecordedRequests);
        Assert.Equal("PROBE:TRUE", result);
    }

    /// <summary>
    /// PART 16: ProbePortAsync proxy regression when daemon is offline:
    /// Proxy is available and returns 200 OK. Daemon is offline (port not listening).
    /// ProbePortAsync must NOT contact the proxy and must NOT return true based on proxy spoofing.
    /// </summary>
    [Fact]
    public async Task ProbeProxyRegression_WhenDaemonOffline_ProxyCannotSpoofProbe_InFreshProcess()
    {
        int offlinePort;
        using (var temp = new TcpListener(IPAddress.Loopback, 0))
        {
            temp.Start();
            offlinePort = ((IPEndPoint)temp.LocalEndpoint).Port;
            temp.Stop();
        }

        await using var proxyP = new TestHttpServer();
        // Proxy returns 200 OK, attempting to spoof active daemon status
        proxyP.ResponseFactory = _ => (200, null, "application/json", "{}");

        var result = await _childFixture.RunChildProcessAsync(
            targetPort: offlinePort,
            protocol: "http",
            token: SyntheticCsrfToken,
            mode: "probe",
            proxyPort: proxyP.Port,
            unhardenedProxy: false);

        // Proxy must never have been contacted
        Assert.Empty(proxyP.RecordedRequests);

        // Probe must return FALSE because the actual daemon port is offline
        Assert.Equal("PROBE:FALSE", result);
    }

    /// <summary>
    /// PART 17: Redirect + Proxy combination test (Fresh Process):
    /// Proxy environment configured + Daemon A returns 307 redirect pointing to B.
    /// Verifies proxy is bypassed (proxy P count = 0), Daemon A is contacted (count = 1),
    /// redirect target B is never contacted (count = 0), and redirect is rejected.
    /// </summary>
    [Fact]
    public async Task RedirectAndProxyCombination_BothControlsEnforced_InFreshProcess()
    {
        await using var daemonA = new TestHttpServer();
        await using var proxyP = new TestHttpServer();
        await using var targetB = new TestHttpServer();

        daemonA.ResponseFactory = _ => (307, targetB.BaseUrl + "/redirect-target", "text/plain", "Redirect");
        proxyP.ResponseFactory = _ => (200, null, "application/json", "{}");
        targetB.ResponseFactory = _ => (200, null, "application/json", "{}");

        var result = await _childFixture.RunChildProcessAsync(
            targetPort: daemonA.Port,
            protocol: "http",
            token: SyntheticCsrfToken,
            mode: "rpc",
            proxyPort: proxyP.Port,
            unhardenedProxy: false);

        // Proxy received zero requests
        Assert.Empty(proxyP.RecordedRequests);

        // Daemon A received the initial request directly
        Assert.Single(daemonA.RecordedRequests);

        // Redirect destination B received zero requests
        Assert.Empty(targetB.RecordedRequests);

        // Result indicates redirect rejection
        Assert.StartsWith("ERROR:HttpRequestException", result);
        Assert.Contains("redirect", result, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // Retained B2 Redirect Regressions from PROMPT #040
    // =========================================================================

    [Fact]
    public async Task B2_OriginalVulnerability_Reproduction_ShowsTokenLeakedWhenRedirectsEnabled()
    {
        await using var serverA = new TestHttpServer();
        await using var serverB = new TestHttpServer();

        serverA.ResponseFactory = req => (307, serverB.BaseUrl + "/redirected", "text/plain", "Redirecting");
        serverB.ResponseFactory = req => (200, null, "application/json", "{\"userStatus\":{\"email\":\"leaked@example.com\"}}");

        var unhardenedHandler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true
        };
        using var unhardenedClient = new AG2RpcClient(unhardenedHandler);

        var result = await unhardenedClient.GetUserStatusAsync(serverA.Port, "http", SyntheticCsrfToken);

        Assert.Single(serverA.RecordedRequests);
        Assert.Single(serverB.RecordedRequests);
        var leakedRequest = serverB.RecordedRequests[0];
        Assert.Equal("POST", leakedRequest.Method);
        Assert.True(leakedRequest.Headers.ContainsKey("x-codeium-csrf-token"));
        Assert.Equal(SyntheticCsrfToken, leakedRequest.Headers["x-codeium-csrf-token"]);
        Assert.Equal("/redirected", leakedRequest.PathAndQuery);
    }

    [Fact]
    public async Task ProductionDefaultHandler_DisablesAutoRedirect_AndRejectsRedirects()
    {
        var handler = AG2RpcClient.CreateDefaultHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);

        await using var serverA = new TestHttpServer();
        await using var serverB = new TestHttpServer();

        serverA.ResponseFactory = _ => (307, serverB.BaseUrl + "/redirected", "text/plain", "Redirecting");
        serverB.ResponseFactory = _ => (200, null, "application/json", "{\"userStatus\":{\"email\":\"leaked@example.com\"}}");

        using var prodClient = new AG2RpcClient();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            prodClient.GetUserStatusAsync(serverA.Port, "http", SyntheticCsrfToken));

        Assert.Equal(HttpStatusCode.TemporaryRedirect, ex.StatusCode);
        Assert.Contains("redirect", ex.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Single(serverA.RecordedRequests);
        Assert.Equal(SyntheticCsrfToken, serverA.RecordedRequests[0].Headers["x-codeium-csrf-token"]);
        Assert.Empty(serverB.RecordedRequests);
    }

    [Theory]
    [InlineData(300, HttpStatusCode.MultipleChoices)]
    [InlineData(301, HttpStatusCode.MovedPermanently)]
    [InlineData(302, HttpStatusCode.Found)]
    [InlineData(303, HttpStatusCode.SeeOther)]
    [InlineData(307, HttpStatusCode.TemporaryRedirect)]
    [InlineData(308, HttpStatusCode.PermanentRedirect)]
    public async Task RedirectStatusCodes_AreAllRejected_WithZeroFollow(int statusCode, HttpStatusCode expectedStatus)
    {
        await using var serverA = new TestHttpServer();
        await using var serverB = new TestHttpServer();

        serverA.ResponseFactory = _ => (statusCode, serverB.BaseUrl + "/target", "text/plain", "Redirect");
        serverB.ResponseFactory = _ => (200, null, "application/json", "{}");

        using var client = new AG2RpcClient();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetUserStatusAsync(serverA.Port, "http", SyntheticCsrfToken));

        Assert.Equal(expectedStatus, ex.StatusCode);
        Assert.Contains("redirect", ex.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Single(serverA.RecordedRequests);
        Assert.Equal("POST", serverA.RecordedRequests[0].Method);
        Assert.Empty(serverB.RecordedRequests);
    }

    [Fact]
    public async Task RedirectTargetMatrix_SameHostDifferentPort_IsRejected()
    {
        await using var serverA = new TestHttpServer();
        await using var serverB = new TestHttpServer();

        serverA.ResponseFactory = _ => (302, $"http://127.0.0.1:{serverB.Port}/other-port", "text/plain", "Redirect");

        using var client = new AG2RpcClient();
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetUserStatusAsync(serverA.Port, "http", SyntheticCsrfToken));

        Assert.Equal(HttpStatusCode.Found, ex.StatusCode);
        Assert.Empty(serverB.RecordedRequests);
    }

    [Fact]
    public async Task RedirectTargetMatrix_LocalhostHostname_IsRejected()
    {
        await using var serverA = new TestHttpServer();
        await using var serverB = new TestHttpServer();

        serverA.ResponseFactory = _ => (301, $"http://localhost:{serverB.Port}/localhost-target", "text/plain", "Redirect");

        using var client = new AG2RpcClient();
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetUserStatusAsync(serverA.Port, "http", SyntheticCsrfToken));

        Assert.Equal(HttpStatusCode.MovedPermanently, ex.StatusCode);
        Assert.Empty(serverB.RecordedRequests);
    }

    [Fact]
    public async Task RedirectTargetMatrix_RelativeLocation_IsRejected()
    {
        await using var serverA = new TestHttpServer();

        serverA.ResponseFactory = _ => (302, "/relative-endpoint", "text/plain", "Redirect");

        using var client = new AG2RpcClient();
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetUserStatusAsync(serverA.Port, "http", SyntheticCsrfToken));

        Assert.Equal(HttpStatusCode.Found, ex.StatusCode);
        Assert.Single(serverA.RecordedRequests);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task ProbePortAsync_RejectsAllRedirects_ReturnsFalse(int redirectStatus)
    {
        await using var serverA = new TestHttpServer();
        await using var serverB = new TestHttpServer();

        serverA.ResponseFactory = _ => (redirectStatus, serverB.BaseUrl + "/probe-target", "text/plain", "Redirect");

        using var client = new AG2RpcClient();
        bool active = await client.ProbePortAsync(serverA.Port, "http", SyntheticCsrfToken);

        Assert.False(active, $"ProbePortAsync must return false when endpoint returns HTTP {redirectStatus}");
        Assert.Empty(serverB.RecordedRequests);
    }

    [Fact]
    public async Task ProbePortAsync_ReturnsTrueFor200And400_ReturnsFalseFor500()
    {
        await using var server = new TestHttpServer();
        using var client = new AG2RpcClient();

        server.ResponseFactory = _ => (200, null, "application/json", "{}");
        Assert.True(await client.ProbePortAsync(server.Port, "http", SyntheticCsrfToken));

        server.ResponseFactory = _ => (400, null, "application/json", "{}");
        Assert.True(await client.ProbePortAsync(server.Port, "http", SyntheticCsrfToken));

        server.ResponseFactory = _ => (500, null, "application/json", "{}");
        Assert.False(await client.ProbePortAsync(server.Port, "http", SyntheticCsrfToken));
    }

    [Fact]
    public async Task DirectRpc_HappyPath_SucceedsWithProperHeadersAndPayload()
    {
        await using var server = new TestHttpServer();

        server.ResponseFactory = req =>
        {
            Assert.Equal("POST", req.Method);
            Assert.Equal("/exa.language_server_pb.LanguageServerService/GetUserStatus", req.PathAndQuery);
            Assert.Equal("1", req.Headers["Connect-Protocol-Version"]);
            Assert.Equal(SyntheticCsrfToken, req.Headers["x-codeium-csrf-token"]);
            Assert.StartsWith("application/json", req.Headers["Content-Type"]);
            Assert.Contains("antigravity", req.Body);

            return (200, null, "application/json", "{\"userStatus\":{\"email\":\"user@example.com\",\"name\":\"Test User\"}}");
        };

        using var client = new AG2RpcClient();
        var result = await client.GetUserStatusAsync(server.Port, "http", SyntheticCsrfToken);

        Assert.NotNull(result);
        Assert.NotNull(result.UserStatus);
        Assert.Equal("user@example.com", result.UserStatus.Email);
        Assert.Equal("Test User", result.UserStatus.Name);
    }

    [Fact]
    public async Task NonRedirectErrors_ThrowWithSanitizedMessage()
    {
        await using var server = new TestHttpServer();
        server.ResponseFactory = _ => (500, null, "text/plain", $"Internal Error with secret: {SyntheticCsrfToken}");

        using var client = new AG2RpcClient();
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetUserStatusAsync(server.Port, "http", SyntheticCsrfToken));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.DoesNotContain(SyntheticCsrfToken, ex.Message);
        Assert.Contains("[REDACTED]", ex.Message);
    }

    [Fact]
    public async Task NonRedirectErrors_WhenResponseBodyIsJson_SanitizesSensitiveFieldsWithoutLeakingSecrets()
    {
        await using var server = new TestHttpServer();
        server.ResponseFactory = _ => (500, null, "application/json",
            $"{{\"error\": \"Unauthorized\", \"csrfToken\": \"{SyntheticCsrfToken}\", \"accessToken\": \"SYNTHETIC_ACCESS_SECRET\"}}");

        using var client = new AG2RpcClient();
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetUserStatusAsync(server.Port, "http", SyntheticCsrfToken));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.DoesNotContain(SyntheticCsrfToken, ex.Message);
        Assert.DoesNotContain("SYNTHETIC_ACCESS_SECRET", ex.Message);
        Assert.Contains("\"csrfToken\": \"[REDACTED]\"", ex.Message);
        Assert.Contains("\"accessToken\": \"[REDACTED]\"", ex.Message);
        Assert.Contains("Unauthorized", ex.Message);
    }

    [Fact]
    public async Task EndpointValidation_EnforcesStrictLoopbackAndValidPorts()
    {
        using var client = new AG2RpcClient();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.GetUserStatusAsync(0, "http", SyntheticCsrfToken));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.GetUserStatusAsync(70000, "http", SyntheticCsrfToken));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.GetUserStatusAsync(5000, "ftp", SyntheticCsrfToken));
    }

    [Fact]
    public async Task InjectedHandler_ReturningRedirect_IsExplicitlyRejectedByClient()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = _ => new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
            {
                Headers = { Location = new Uri("http://127.0.0.1:9999/redirect") }
            }
        };

        using var client = new AG2RpcClient(mockHandler);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetUserStatusAsync(51768, "https", SyntheticCsrfToken));

        Assert.Equal(HttpStatusCode.TemporaryRedirect, ex.StatusCode);
        Assert.Contains("redirect", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetCascadeTrajectoryGeneratorMetadataAsync_RejectsRedirect()
    {
        await using var serverA = new TestHttpServer();
        await using var serverB = new TestHttpServer();

        serverA.ResponseFactory = _ => (307, serverB.BaseUrl + "/metadata", "text/plain", "Redirect");

        using var client = new AG2RpcClient();
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetCascadeTrajectoryGeneratorMetadataAsync(serverA.Port, "http", SyntheticCsrfToken, "cascade-1"));

        Assert.Equal(HttpStatusCode.TemporaryRedirect, ex.StatusCode);
        Assert.Empty(serverB.RecordedRequests);
    }

    [Fact]
    public async Task GetAllCascadeTrajectoriesAsync_RejectsRedirect()
    {
        await using var serverA = new TestHttpServer();
        await using var serverB = new TestHttpServer();

        serverA.ResponseFactory = _ => (308, serverB.BaseUrl + "/trajectories", "text/plain", "Redirect");

        using var client = new AG2RpcClient();
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAllCascadeTrajectoriesAsync(serverA.Port, "http", SyntheticCsrfToken));

        Assert.Equal(HttpStatusCode.PermanentRedirect, ex.StatusCode);
        Assert.Empty(serverB.RecordedRequests);
    }

    // =========================================================================
    // PROMPT #044 Regressions: Unified Bounded RPC Deadline for Body Reads
    // =========================================================================

    [Fact]
    public async Task ProductionDefault_GetUserStatusAsync_200WithheldBody_TimesOutAtConfigured5Seconds()
    {
        await using var server = new TestHttpServer();
        server.CustomResponseWriter = async (req, stream, ct) =>
        {
            var header = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 5000\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
            await stream.FlushAsync(ct);
            await Task.Delay(10000, ct);
        };

        using var client = new AG2RpcClient();
        var sw = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<TaskCanceledException>(() =>
            client.GetUserStatusAsync(server.Port, "http", SyntheticCsrfToken));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 4800,
            $"Expected timeout to wait for full 5s deadline, but completed in {sw.ElapsedMilliseconds}ms");
        Assert.True(sw.ElapsedMilliseconds < 7000,
            $"Expected timeout to fire near 5s, but took {sw.ElapsedMilliseconds}ms (must NOT be pending past 6.5s)");
        Assert.Contains("timed out after 5 seconds", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<TimeoutException>(ex.InnerException);
    }

    [Fact]
    public async Task ProductionDefault_GetAllCascadeTrajectoriesAsync_200WithheldBody_TimesOutAtConfigured5Seconds()
    {
        await using var server = new TestHttpServer();
        server.CustomResponseWriter = async (req, stream, ct) =>
        {
            var header = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 5000\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
            await stream.FlushAsync(ct);
            await Task.Delay(10000, ct);
        };

        using var client = new AG2RpcClient();
        var sw = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<TaskCanceledException>(() =>
            client.GetAllCascadeTrajectoriesAsync(server.Port, "http", SyntheticCsrfToken));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 4800,
            $"Expected timeout to wait for full 5s deadline, but completed in {sw.ElapsedMilliseconds}ms");
        Assert.True(sw.ElapsedMilliseconds < 7000,
            $"Expected timeout to fire near 5s, but took {sw.ElapsedMilliseconds}ms (must NOT be pending past 6.5s)");
        Assert.Contains("timed out after 5 seconds", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<TimeoutException>(ex.InnerException);
    }

    [Fact]
    public async Task ProductionDefault_GetCascadeTrajectoryGeneratorMetadataAsync_200WithheldBody_TimesOutAtConfigured5Seconds()
    {
        await using var server = new TestHttpServer();
        server.CustomResponseWriter = async (req, stream, ct) =>
        {
            var header = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 5000\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
            await stream.FlushAsync(ct);
            await Task.Delay(10000, ct);
        };

        using var client = new AG2RpcClient();
        var sw = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<TaskCanceledException>(() =>
            client.GetCascadeTrajectoryGeneratorMetadataAsync(server.Port, "http", SyntheticCsrfToken, "casc-1"));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 4800,
            $"Expected timeout to wait for full 5s deadline, but completed in {sw.ElapsedMilliseconds}ms");
        Assert.True(sw.ElapsedMilliseconds < 7000,
            $"Expected timeout to fire near 5s, but took {sw.ElapsedMilliseconds}ms (must NOT be pending past 6.5s)");
        Assert.Contains("timed out after 5 seconds", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<TimeoutException>(ex.InnerException);
    }

    [Fact]
    public async Task ProductionDefault_GetUserStatusAsync_500WithheldBody_TimesOutAtConfigured5Seconds()
    {
        await using var server = new TestHttpServer();
        server.CustomResponseWriter = async (req, stream, ct) =>
        {
            var header = "HTTP/1.1 500 Internal Server Error\r\nContent-Type: text/plain\r\nContent-Length: 5000\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
            await stream.FlushAsync(ct);
            await Task.Delay(10000, ct);
        };

        using var client = new AG2RpcClient();
        var sw = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<TaskCanceledException>(() =>
            client.GetUserStatusAsync(server.Port, "http", SyntheticCsrfToken));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 4800,
            $"Expected timeout to wait for full 5s deadline, but completed in {sw.ElapsedMilliseconds}ms");
        Assert.True(sw.ElapsedMilliseconds < 7000,
            $"Expected timeout to fire near 5s, but took {sw.ElapsedMilliseconds}ms (must NOT be pending past 6.5s)");
        Assert.Contains("timed out after 5 seconds", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<TimeoutException>(ex.InnerException);
    }

    [Fact]
    public async Task SingleDeadlineBudget_HeadersDelayAndStalledBody_ShareSingleBudget()
    {
        await using var server = new TestHttpServer();
        server.CustomResponseWriter = async (req, stream, ct) =>
        {
            // Delay sending headers by 500ms
            await Task.Delay(500, ct);
            var header = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 5000\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
            await stream.FlushAsync(ct);
            // Withhold body bytes
            await Task.Delay(10000, ct);
        };

        // Budget is 1200ms total. Headers consume 500ms. Body gets remaining ~700ms.
        // If body read incorrectly reset the deadline to 1200ms, total would be 500 + 1200 = 1700ms+.
        using var client = new AG2RpcClient(rpcTimeout: TimeSpan.FromMilliseconds(1200));
        var sw = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<TaskCanceledException>(() =>
            client.GetUserStatusAsync(server.Port, "http", SyntheticCsrfToken));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 1100,
            $"Expected total elapsed time >= 1100ms, but was {sw.ElapsedMilliseconds}ms");
        Assert.True(sw.ElapsedMilliseconds < 1600,
            $"Expected single budget termination < 1600ms, but took {sw.ElapsedMilliseconds}ms");
        Assert.IsType<TimeoutException>(ex.InnerException);
    }

    [Fact]
    public async Task CallerCancellation_TrumpsTransportTimeout_ImmediatelyTerminates()
    {
        await using var server = new TestHttpServer();
        server.CustomResponseWriter = async (req, stream, ct) =>
        {
            var header = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 5000\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
            await stream.FlushAsync(ct);
            await Task.Delay(10000, ct);
        };

        using var client = new AG2RpcClient(rpcTimeout: TimeSpan.FromSeconds(5));
        using var callerCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var sw = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetUserStatusAsync(server.Port, "http", SyntheticCsrfToken, callerCts.Token));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"Expected caller cancellation to terminate immediately (< 1000ms), but took {sw.ElapsedMilliseconds}ms");
        Assert.True(callerCts.IsCancellationRequested);
    }

    [Theory]
    [InlineData("GetUserStatus")]
    [InlineData("GetAllCascadeTrajectories")]
    [InlineData("GetCascadeTrajectoryGeneratorMetadata")]
    [InlineData("GetUserStatus_500")]
    public async Task FastTimeoutMatrix_AllEndpointsTimeOutOnStalledBody(string endpoint)
    {
        await using var server = new TestHttpServer();
        server.CustomResponseWriter = async (req, stream, ct) =>
        {
            int statusCode = endpoint == "GetUserStatus_500" ? 500 : 200;
            string statusText = statusCode == 500 ? "Internal Server Error" : "OK";
            var header = $"HTTP/1.1 {statusCode} {statusText}\r\nContent-Type: application/json\r\nContent-Length: 5000\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
            await stream.FlushAsync(ct);
            await Task.Delay(10000, ct);
        };

        using var client = new AG2RpcClient(rpcTimeout: TimeSpan.FromMilliseconds(400));
        var sw = Stopwatch.StartNew();

        Task testTask = endpoint switch
        {
            "GetUserStatus" => client.GetUserStatusAsync(server.Port, "http", SyntheticCsrfToken),
            "GetAllCascadeTrajectories" => client.GetAllCascadeTrajectoriesAsync(server.Port, "http", SyntheticCsrfToken),
            "GetCascadeTrajectoryGeneratorMetadata" => client.GetCascadeTrajectoryGeneratorMetadataAsync(server.Port, "http", SyntheticCsrfToken, "casc-1"),
            "GetUserStatus_500" => client.GetUserStatusAsync(server.Port, "http", SyntheticCsrfToken),
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint))
        };

        var ex = await Assert.ThrowsAsync<TaskCanceledException>(() => testTask);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 350,
            $"Expected timeout >= 350ms, but was {sw.ElapsedMilliseconds}ms");
        Assert.True(sw.ElapsedMilliseconds < 1200,
            $"Expected timeout < 1200ms, but was {sw.ElapsedMilliseconds}ms");
        Assert.IsType<TimeoutException>(ex.InnerException);
    }

    [Fact]
    public async Task FreshChildProcess_ProductionDefault_StalledBody_TerminatesAt5Seconds()
    {
        await using var server = new TestHttpServer();
        server.CustomResponseWriter = async (req, stream, ct) =>
        {
            var header = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 5000\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
            await stream.FlushAsync(ct);
            await Task.Delay(10000, ct);
        };

        var sw = Stopwatch.StartNew();
        var result = await _childFixture.RunChildProcessAsync(
            targetPort: server.Port,
            protocol: "http",
            token: SyntheticCsrfToken,
            mode: "rpc");
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 4800,
            $"Expected child process to run for full 5s timeout, but elapsed {sw.ElapsedMilliseconds}ms");
        Assert.True(sw.ElapsedMilliseconds < 7500,
            $"Expected child process to terminate near 5s, but elapsed {sw.ElapsedMilliseconds}ms");
        Assert.StartsWith("ERROR:TaskCanceledException", result);
        Assert.Contains("timed out after 5 seconds", result, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies that when a daemon responds with HTTP 500 containing a JSON secret with mixed quotes,
    /// AG2RpcClient's exception message redacts the secret without leaking suffixes.
    /// </summary>
    [Fact]
    public async Task Http500_MixedQuotesSecret_IsSanitizedInClientException()
    {
        await using var server = new TestHttpServer();
        server.ResponseFactory = _ => (500, null, "application/json", "{\"password\":\"SYN_RPC_PREFIX'SYN_RPC_SUFFIX\"}");

        using var client = new AG2RpcClient();
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetUserStatusAsync(server.Port, "http", SyntheticCsrfToken));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.DoesNotContain("SYN_RPC_PREFIX", ex.Message);
        Assert.DoesNotContain("SYN_RPC_SUFFIX", ex.Message);
        Assert.Contains("[REDACTED]", ex.Message);
    }

    /// <summary>
    /// Verifies that when a daemon responds with HTTP 500 containing a JSON authorization property with non-Bearer scheme,
    /// AG2RpcClient's exception message redacts the credentials.
    /// </summary>
    [Fact]
    public async Task Http500_NonBearerAuthorizationSecret_IsSanitizedInClientException()
    {
        await using var server = new TestHttpServer();
        server.ResponseFactory = _ => (500, null, "application/json", "{\"authorization\":\"Basic SYN_RPC_BASIC\"}");

        using var client = new AG2RpcClient();
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetUserStatusAsync(server.Port, "http", SyntheticCsrfToken));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.DoesNotContain("SYN_RPC_BASIC", ex.Message);
        Assert.Contains("[REDACTED]", ex.Message);
    }

    /// <summary>
    /// Verifies that when a daemon responds with HTTP 500 containing a multiline secret with raw newlines,
    /// AG2RpcClient's exception message redacts the secret without leaking suffixes across newlines.
    /// </summary>
    [Fact]
    public async Task Http500_MultilineRawNewlineSecret_IsSanitizedInClientException()
    {
        await using var server = new TestHttpServer();
        server.ResponseFactory = _ => (500, null, "application/json", "{\"password\":\"SYN_RPC_PREFIX\nSYN_RPC_SUFFIX\"}");

        using var client = new AG2RpcClient();
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetUserStatusAsync(server.Port, "http", SyntheticCsrfToken));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.DoesNotContain("SYN_RPC_PREFIX", ex.Message);
        Assert.DoesNotContain("SYN_RPC_SUFFIX", ex.Message);
        Assert.Contains("[REDACTED]", ex.Message);
    }

    /// <summary>
    /// R1: Verifies that when a daemon responds with HTTP 500 containing whitespace (LF) after the colon
    /// in a JSON secret property, AG2RpcClient's exception message redacts the secret without leaking suffixes.
    /// </summary>
    [Fact]
    public async Task Http500_R1_WhitespaceAfterColon_IsSanitizedInClientException()
    {
        await using var server = new TestHttpServer();
        server.ResponseFactory = _ => (500, null, "application/json", "{\"password\":\n  \"SYN_RPC_WHITESPACE_SECRET\",\"status\":\"failed\"}");

        using var client = new AG2RpcClient();
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetUserStatusAsync(server.Port, "http", SyntheticCsrfToken));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.DoesNotContain("SYN_RPC_WHITESPACE_SECRET", ex.Message);
        Assert.Contains("[REDACTED]", ex.Message);
        Assert.Contains("status", ex.Message);
        Assert.Contains("failed", ex.Message);
    }

    /// <summary>
    /// R1: Verifies that when a daemon responds with HTTP 500 containing whitespace (LF) before the colon
    /// in a JSON secret property, AG2RpcClient's exception message redacts the secret.
    /// </summary>
    [Fact]
    public async Task Http500_R1_WhitespaceBeforeColon_IsSanitizedInClientException()
    {
        await using var server = new TestHttpServer();
        server.ResponseFactory = _ => (500, null, "application/json", "{\"password\"\n: \"SYN_RPC_LF_BEFORE_COLON\",\"status\":\"failed\"}");

        using var client = new AG2RpcClient();
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetUserStatusAsync(server.Port, "http", SyntheticCsrfToken));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.DoesNotContain("SYN_RPC_LF_BEFORE_COLON", ex.Message);
        Assert.Contains("[REDACTED]", ex.Message);
        Assert.Contains("status", ex.Message);
        Assert.Contains("failed", ex.Message);
    }

    /// <summary>
    /// R2: Verifies that when a daemon responds with HTTP 500 containing a comma-separated Digest
    /// Authorization header, AG2RpcClient redacts the entire credential-bearing value and preserves X-Trace.
    /// </summary>
    [Fact]
    public async Task Http500_R2_DigestAuthorizationHeader_IsSanitizedInClientException()
    {
        await using var server = new TestHttpServer();
        server.ResponseFactory = _ => (500, null, "text/plain", "Authorization: Digest username=\"SYN_RPC_USER\", response=\"SYN_RPC_DIGEST_RESPONSE\", nonce=\"SYN_RPC_NONCE\"\r\nX-Trace: ok");

        using var client = new AG2RpcClient();
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetUserStatusAsync(server.Port, "http", SyntheticCsrfToken));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.DoesNotContain("SYN_RPC_USER", ex.Message);
        Assert.DoesNotContain("SYN_RPC_DIGEST_RESPONSE", ex.Message);
        Assert.DoesNotContain("SYN_RPC_NONCE", ex.Message);
        Assert.Contains("[REDACTED]", ex.Message);
        Assert.Contains("X-Trace: ok", ex.Message);
    }

    /// <summary>
    /// F1: Verifies that when an upstream daemon responds with HTTP 500 containing a Digest
    /// Authorization header whose realm contains brackets (e.g. realm="service[team]"),
    /// AG2RpcClient does not terminate redaction prematurely at the bracket and scrubs all trailing
    /// credentials (SYN_RPC_USER, SYN_RPC_NONCE, SYN_RPC_RESPONSE) while preserving nonsensitive headers.
    /// </summary>
    [Fact]
    public async Task Http500_F1_DigestAuthorizationWithBracketRealm_IsSanitizedInClientException()
    {
        await using var server = new TestHttpServer();
        server.ResponseFactory = _ => (500, null, "text/plain", "Authorization: Digest username=\"SYN_RPC_USER\", realm=\"service[team]\", nonce=\"SYN_RPC_NONCE\", response=\"SYN_RPC_RESPONSE\"\r\nX-Trace: ok");

        using var client = new AG2RpcClient();
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetUserStatusAsync(server.Port, "http", SyntheticCsrfToken));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.DoesNotContain("SYN_RPC_USER", ex.Message);
        Assert.DoesNotContain("SYN_RPC_NONCE", ex.Message);
        Assert.DoesNotContain("SYN_RPC_RESPONSE", ex.Message);
        Assert.Contains("[REDACTED]", ex.Message);
        Assert.Contains("X-Trace: ok", ex.Message);
    }

    /// <summary>
    /// F1: Verifies that when an upstream daemon responds with HTTP 500 containing a Digest
    /// Authorization header whose realm contains braces (e.g. realm="service{team}"),
    /// AG2RpcClient does not terminate redaction prematurely at the brace and scrubs all trailing
    /// credentials (SYN_RPC_USER, SYN_RPC_NONCE, SYN_RPC_RESPONSE) while preserving nonsensitive headers.
    /// </summary>
    [Fact]
    public async Task Http500_F1_DigestAuthorizationWithBraceRealm_IsSanitizedInClientException()
    {
        await using var server = new TestHttpServer();
        server.ResponseFactory = _ => (500, null, "text/plain", "Authorization: Digest username=\"SYN_RPC_USER\", realm=\"service{team}\", nonce=\"SYN_RPC_NONCE\", response=\"SYN_RPC_RESPONSE\"\r\nX-Trace: ok");

        using var client = new AG2RpcClient();
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetUserStatusAsync(server.Port, "http", SyntheticCsrfToken));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.DoesNotContain("SYN_RPC_USER", ex.Message);
        Assert.DoesNotContain("SYN_RPC_NONCE", ex.Message);
        Assert.DoesNotContain("SYN_RPC_RESPONSE", ex.Message);
        Assert.Contains("[REDACTED]", ex.Message);
        Assert.Contains("X-Trace: ok", ex.Message);
    }
}

/// <summary>
/// Compiles once a small console helper project that instantiates AG2RpcClient in a fresh OS process,
/// allowing tests to execute genuine child processes with isolated proxy environment variables
/// (HTTP_PROXY, http_proxy, ALL_PROXY, etc.) without process-level proxy cache interference.
/// </summary>
public sealed class RpcChildProcessFixture : IDisposable
{
    private readonly string _workspace;
    public string DotNetExePath { get; }
    public string HelperDllPath { get; }

    public RpcChildProcessFixture()
    {
        DotNetExePath = ResolveDotNetExe();
        string repoRoot = LocateRepoRoot();
        _workspace = Path.Combine(Path.GetTempPath(), $"ag2_rpc_helper_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_workspace, "helper"));

        string coreProject = Path.Combine(repoRoot, "dotnet", "src", "AG2Router.Core", "AG2Router.Core.csproj");
        string ag2Project = Path.Combine(repoRoot, "dotnet", "src", "AG2Router.AG2", "AG2Router.AG2.csproj");

        File.WriteAllText(
            Path.Combine(_workspace, "helper", "helper.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="{CORE}" />
                <ProjectReference Include="{AG2}" />
              </ItemGroup>
            </Project>
            """.Replace("{CORE}", coreProject).Replace("{AG2}", ag2Project));

        File.WriteAllText(Path.Combine(_workspace, "helper", "Program.cs"), ChildProgramSource);

        string outputDir = Path.Combine(_workspace, "out");
        var buildInfo = new ProcessStartInfo(DotNetExePath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        buildInfo.ArgumentList.Add("build");
        buildInfo.ArgumentList.Add(Path.Combine(_workspace, "helper", "helper.csproj"));
        buildInfo.ArgumentList.Add("-c");
        buildInfo.ArgumentList.Add("Release");
        buildInfo.ArgumentList.Add("-o");
        buildInfo.ArgumentList.Add(outputDir);
        buildInfo.ArgumentList.Add("--nologo");
        buildInfo.ArgumentList.Add("-v");
        buildInfo.ArgumentList.Add("quiet");

        using var build = Process.Start(buildInfo) ?? throw new InvalidOperationException("dotnet build could not start.");
        if (!build.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            build.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Rpc child helper build timed out.");
        }
        if (build.ExitCode != 0)
        {
            throw new InvalidOperationException("Rpc child helper build failed: " + build.StandardError.ReadToEnd());
        }

        HelperDllPath = Path.Combine(outputDir, "helper.dll");
    }

    public async Task<string> RunChildProcessAsync(
        int targetPort,
        string protocol,
        string token,
        string mode,
        int? proxyPort = null,
        bool unhardenedProxy = false,
        string proxyEnvVar = "HTTP_PROXY",
        bool setBothCasings = true)
    {
        string resultFile = Path.Combine(_workspace, $"result-{Guid.NewGuid():N}.txt");

        var psi = new ProcessStartInfo(DotNetExePath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(HelperDllPath);
        psi.ArgumentList.Add("--port"); psi.ArgumentList.Add(targetPort.ToString());
        psi.ArgumentList.Add("--protocol"); psi.ArgumentList.Add(protocol);
        psi.ArgumentList.Add("--token"); psi.ArgumentList.Add(token);
        psi.ArgumentList.Add("--mode"); psi.ArgumentList.Add(mode);
        psi.ArgumentList.Add("--result"); psi.ArgumentList.Add(resultFile);

        if (unhardenedProxy)
        {
            psi.ArgumentList.Add("--unhardened-proxy");
        }

        // Configure proxy environment variables on the fresh process
        if (proxyPort.HasValue)
        {
            psi.ArgumentList.Add("--proxy-port"); psi.ArgumentList.Add(proxyPort.Value.ToString());
            string proxyUri = $"http://127.0.0.1:{proxyPort.Value}";
            psi.EnvironmentVariables[proxyEnvVar] = proxyUri;
            if (setBothCasings)
            {
                psi.EnvironmentVariables["HTTP_PROXY"] = proxyUri;
                psi.EnvironmentVariables["http_proxy"] = proxyUri;
                psi.EnvironmentVariables["ALL_PROXY"] = proxyUri;
                psi.EnvironmentVariables["all_proxy"] = proxyUri;
                psi.EnvironmentVariables["HTTPS_PROXY"] = proxyUri;
                psi.EnvironmentVariables["https_proxy"] = proxyUri;
            }
        }
        psi.EnvironmentVariables.Remove("NO_PROXY");
        psi.EnvironmentVariables.Remove("no_proxy");

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Child process could not start.");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

        if (!File.Exists(resultFile))
        {
            string stderr = await process.StandardError.ReadToEndAsync(cts.Token).ConfigureAwait(false);
            throw classInvalidOperationException($"Child process produced no result file. Stderr: {stderr}");
        }

        return await File.ReadAllTextAsync(resultFile, cts.Token).ConfigureAwait(false);
    }

    private static InvalidOperationException classInvalidOperationException(string msg) => new(msg);

    private static string ChildProgramSource => """
        using System;
        using System.Collections.Generic;
        using System.IO;
        using System.Net;
        using System.Net.Http;
        using System.Threading.Tasks;
        using AG2Router.AG2.Rpc;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            map[args[i]] = args[i + 1];
        }

        int port = int.Parse(map["--port"]);
        string protocol = map.GetValueOrDefault("--protocol", "http");
        string token = map.GetValueOrDefault("--token", "test-token");
        string mode = map.GetValueOrDefault("--mode", "rpc");
        string resultFile = map["--result"];
        bool unhardened = map.ContainsKey("--unhardened-proxy");
        int? proxyPort = map.TryGetValue("--proxy-port", out var ppStr) && int.TryParse(ppStr, out var pp) ? pp : null;

        try
        {
            using var client = unhardened
                ? new AG2RpcClient(new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    UseProxy = true,
                    Proxy = proxyPort.HasValue
                        ? new WebProxy($"http://127.0.0.1:{proxyPort.Value}") { BypassProxyOnLocal = false }
                        : null
                })
                : new AG2RpcClient();

            if (mode == "probe")
            {
                bool probeResult = await client.ProbePortAsync(port, protocol, token);
                File.WriteAllText(resultFile, $"PROBE:{(probeResult ? "TRUE" : "FALSE")}");
            }
            else if (mode == "rpc")
            {
                var status = await client.GetUserStatusAsync(port, protocol, token);
                File.WriteAllText(resultFile, $"RPC:OK:{status?.UserStatus?.Email ?? "NULL"}");
            }
        }
        catch (Exception ex)
        {
            File.WriteAllText(resultFile, $"ERROR:{ex.GetType().Name}:{ex.Message}");
        }
        """;

    private static string LocateRepoRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "dotnet", "src", "AG2Router.AG2", "AG2Router.AG2.csproj")))
                return directory;
            directory = Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar));
        }
        throw new InvalidOperationException("Repository root could not be located for the cross-process helper.");
    }

    private static string ResolveDotNetExe()
    {
        string? processPath = Environment.ProcessPath;
        if (processPath is not null &&
            string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            return processPath;

        string? dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (dotnetRoot is not null)
        {
            foreach (string candidate in new[] { "dotnet.exe", "dotnet" })
            {
                string fullPath = Path.Combine(dotnetRoot, candidate);
                if (File.Exists(fullPath)) return fullPath;
            }
        }

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string candidate in new[] { "dotnet.exe", "dotnet" })
            {
                string fullPath = Path.Combine(directory.Trim(), candidate);
                if (File.Exists(fullPath)) return fullPath;
            }
        }

        throw new InvalidOperationException("dotnet.exe could not be located for cross-process tests.");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workspace))
                Directory.Delete(_workspace, recursive: true);
        }
        catch
        {
            // Ignore temp dir cleanup errors
        }
    }
}

/// <summary>
/// Lightweight in-memory loopback HTTP test server using raw TCP sockets to ensure
/// complete control over HTTP status codes, headers, and zero dependency on OS URL ACLs.
/// </summary>
public sealed class TestHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _listenTask;
    private readonly object _lock = new();

    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public List<RecordedRequest> RecordedRequests { get; } = new();

    public Func<RecordedRequest, (int StatusCode, string? Location, string? ContentType, string? Body)>? ResponseFactory { get; set; }

    public Func<RecordedRequest, Stream, CancellationToken, Task>? CustomResponseWriter { get; set; }

    public sealed record RecordedRequest(
        string Method,
        string PathAndQuery,
        Dictionary<string, string> Headers,
        string Body,
        DateTime Timestamp = default);

    public int AcceptedConnectionsCount => _acceptedConnections;
    private int _acceptedConnections;

    public TestHttpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _listenTask = Task.Run(ListenLoopAsync);
    }

    private async Task ListenLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                    Interlocked.Increment(ref _acceptedConnections);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    break;
                }

                _ = HandleClientAsync(client);
            }
        }
        catch
        {
            // Listener shutdown
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                stream.ReadTimeout = 3000;
                stream.WriteTimeout = 3000;

                var buffer = new byte[8192];
                var ms = new MemoryStream();
                int headerEndIndex = -1;

                while (headerEndIndex == -1)
                {
                    int read = await stream.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                    if (read == 0) return;
                    ms.Write(buffer, 0, read);

                    byte[] currentBytes = ms.ToArray();
                    headerEndIndex = FindHeaderEnd(currentBytes);
                }

                byte[] allBytes = ms.ToArray();
                string headerString = Encoding.ASCII.GetString(allBytes, 0, headerEndIndex);
                var lines = headerString.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                if (lines.Length == 0) return;

                var requestLine = lines[0].Split(' ');
                string method = requestLine.Length > 0 ? requestLine[0] : "UNKNOWN";
                string path = requestLine.Length > 1 ? requestLine[1] : "/";

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    int colon = line.IndexOf(':');
                    if (colon > 0)
                    {
                        var key = line[..colon].Trim();
                        var val = line[(colon + 1)..].Trim();
                        headers[key] = val;
                    }
                }

                int contentLength = 0;
                if (headers.TryGetValue("Content-Length", out var clStr) && int.TryParse(clStr, out var cl))
                {
                    contentLength = cl;
                }

                int bodyStart = headerEndIndex;
                int currentBodyBytes = allBytes.Length - bodyStart;
                while (currentBodyBytes < contentLength)
                {
                    int read = await stream.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    ms.Write(buffer, 0, read);
                    currentBodyBytes += read;
                }

                byte[] fullBytes = ms.ToArray();
                string body = string.Empty;
                if (contentLength > 0 && fullBytes.Length >= bodyStart + contentLength)
                {
                    body = Encoding.UTF8.GetString(fullBytes, bodyStart, contentLength);
                }

                var recorded = new RecordedRequest(method, path, headers, body, DateTime.UtcNow);
                lock (_lock)
                {
                    RecordedRequests.Add(recorded);
                }

                if (CustomResponseWriter != null)
                {
                    await CustomResponseWriter(recorded, stream, _cts.Token).ConfigureAwait(false);
                    return;
                }

                var factory = ResponseFactory;
                var (statusCode, location, contentType, responseBody) = factory != null
                    ? factory(recorded)
                    : (200, null, "text/plain", "OK");

                responseBody ??= string.Empty;
                byte[] responseBytes = Encoding.UTF8.GetBytes(responseBody);
                string statusDesc = GetStatusDescription(statusCode);

                var sb = new StringBuilder();
                sb.Append($"HTTP/1.1 {statusCode} {statusDesc}\r\n");
                sb.Append("Connection: close\r\n");
                if (!string.IsNullOrEmpty(location))
                {
                    sb.Append($"Location: {location}\r\n");
                }
                if (!string.IsNullOrEmpty(contentType))
                {
                    sb.Append($"Content-Type: {contentType}\r\n");
                }
                sb.Append($"Content-Length: {responseBytes.Length}\r\n");
                sb.Append("\r\n");

                byte[] headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
                await stream.WriteAsync(headerBytes, _cts.Token).ConfigureAwait(false);
                if (responseBytes.Length > 0)
                {
                    await stream.WriteAsync(responseBytes, _cts.Token).ConfigureAwait(false);
                }
                await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
            }
            catch
            {
                // Ignore socket errors during test client handling
            }
        }
    }

    private static int FindHeaderEnd(byte[] bytes)
    {
        for (int i = 0; i < bytes.Length - 3; i++)
        {
            if (bytes[i] == '\r' && bytes[i + 1] == '\n' && bytes[i + 2] == '\r' && bytes[i + 3] == '\n')
            {
                return i + 4;
            }
        }
        for (int i = 0; i < bytes.Length - 1; i++)
        {
            if (bytes[i] == '\n' && bytes[i + 1] == '\n')
            {
                return i + 2;
            }
        }
        return -1;
    }

    private static string GetStatusDescription(int statusCode) => statusCode switch
    {
        200 => "OK",
        300 => "Multiple Choices",
        301 => "Moved Permanently",
        302 => "Found",
        303 => "See Other",
        304 => "Not Modified",
        307 => "Temporary Redirect",
        308 => "Permanent Redirect",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        500 => "Internal Server Error",
        _ => "Status"
    };

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            await _listenTask.ConfigureAwait(false);
        }
        catch
        {
            // Ignore cancellation on dispose
        }
        _cts.Dispose();
    }
}
