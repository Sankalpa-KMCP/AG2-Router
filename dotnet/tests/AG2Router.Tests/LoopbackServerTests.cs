using System.Net;
using System.Net.Http;
using System.Text.Json;
using AG2Router.App.Server;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

public class LoopbackServerTests
{
    [Fact]
    public async Task LoopbackServer_StartsOnEphemeralPort_AndServesStaticAssets()
    {
        var server = new LoopbackServer();
        try
        {
            await server.StartAsync(0);
            Assert.True(server.BoundPort > 0, "BoundPort should be a valid positive port");
            Assert.StartsWith("http://127.0.0.1:", server.BoundUrl);

            using var httpClient = new HttpClient();

            // 1. Static asset: index.html
            var htmlRes = await httpClient.GetAsync($"{server.BoundUrl}/index.html");
            Assert.Equal(HttpStatusCode.OK, htmlRes.StatusCode);
            var htmlContent = await htmlRes.Content.ReadAsStringAsync();
            Assert.Contains("AG2 Router", htmlContent);

            // 2. Static asset: styles.css
            var cssRes = await httpClient.GetAsync($"{server.BoundUrl}/styles.css");
            Assert.Equal(HttpStatusCode.OK, cssRes.StatusCode);

            // 3. Static asset: app.js
            var jsRes = await httpClient.GetAsync($"{server.BoundUrl}/app.js");
            Assert.Equal(HttpStatusCode.OK, jsRes.StatusCode);
        }
        finally
        {
            await server.StopAsync();
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task LoopbackServer_ServesTruthfulCompatibleApiEndpoints()
    {
        var server = new LoopbackServer();
        try
        {
            await server.StartAsync(0);
            using var httpClient = new HttpClient();

            // 1. GET /api/status
            var statusRes = await httpClient.GetAsync($"{server.BoundUrl}/api/status");
            Assert.Equal(HttpStatusCode.OK, statusRes.StatusCode);
            Assert.Equal("nosniff", statusRes.Headers.GetValues("X-Content-Type-Options").FirstOrDefault());
            Assert.Equal("DENY", statusRes.Headers.GetValues("X-Frame-Options").FirstOrDefault());

            var statusJson = await statusRes.Content.ReadAsStringAsync();
            var statusDoc = JsonDocument.Parse(statusJson);
            Assert.Equal("ok", statusDoc.RootElement.GetProperty("status").GetString());
            Assert.False(statusDoc.RootElement.GetProperty("ag2").GetProperty("connected").GetBoolean());
            Assert.Equal("Waiting for Antigravity 2", statusDoc.RootElement.GetProperty("ag2").GetProperty("message").GetString());
            Assert.Equal("IDLE", statusDoc.RootElement.GetProperty("router").GetProperty("state").GetString());

            // 2. GET /api/accounts
            var accRes = await httpClient.GetAsync($"{server.BoundUrl}/api/accounts");
            Assert.Equal(HttpStatusCode.OK, accRes.StatusCode);
            var accJson = await accRes.Content.ReadAsStringAsync();
            var accDoc = JsonDocument.Parse(accJson);
            Assert.Empty(accDoc.RootElement.GetProperty("accounts").EnumerateArray());

            // 3. GET /api/config
            var cfgRes = await httpClient.GetAsync($"{server.BoundUrl}/api/config");
            Assert.Equal(HttpStatusCode.OK, cfgRes.StatusCode);
            var cfgJson = await cfgRes.Content.ReadAsStringAsync();
            var cfgDoc = JsonDocument.Parse(cfgJson);
            Assert.False(cfgDoc.RootElement.GetProperty("config").GetProperty("autoSwitchEnabled").GetBoolean());

            // 4. GET /api/switching/status
            var swRes = await httpClient.GetAsync($"{server.BoundUrl}/api/switching/status");
            Assert.Equal(HttpStatusCode.OK, swRes.StatusCode);
            var swJson = await swRes.Content.ReadAsStringAsync();
            var swDoc = JsonDocument.Parse(swJson);
            Assert.Equal("IDLE", swDoc.RootElement.GetProperty("status").GetProperty("currentState").GetString());
        }
        finally
        {
            await server.StopAsync();
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task LoopbackServer_MutationEndpointsReturnSafeUnavailableCodes()
    {
        var server = new LoopbackServer();
        try
        {
            await server.StartAsync(0);
            using var httpClient = new HttpClient();

            // POST /api/accounts/enroll-current -> 501 Not Implemented
            var enrollRes = await httpClient.PostAsync($"{server.BoundUrl}/api/accounts/enroll-current", null);
            Assert.Equal(HttpStatusCode.NotImplemented, enrollRes.StatusCode);

            // POST /api/accounts/acc_1/switch-plan -> 501 Not Implemented
            var planRes = await httpClient.PostAsync($"{server.BoundUrl}/api/accounts/acc_1/switch-plan", null);
            Assert.Equal(HttpStatusCode.NotImplemented, planRes.StatusCode);

            // POST /api/accounts/acc_1/switch -> 501 when no native coordinator is configured
            var switchRes = await httpClient.PostAsync($"{server.BoundUrl}/api/accounts/acc_1/switch", null);
            Assert.Equal(HttpStatusCode.NotImplemented, switchRes.StatusCode);

            // DELETE /api/accounts/acc_1 -> 501 Not Implemented
            var delRes = await httpClient.DeleteAsync($"{server.BoundUrl}/api/accounts/acc_1");
            Assert.Equal(HttpStatusCode.NotImplemented, delRes.StatusCode);

            // POST /api/config -> 501 Not Implemented
            var updateCfgRes = await httpClient.PostAsync($"{server.BoundUrl}/api/config", null);
            Assert.Equal(HttpStatusCode.NotImplemented, updateCfgRes.StatusCode);
        }
        finally
        {
            await server.StopAsync();
            await server.DisposeAsync();
        }
    }
}
