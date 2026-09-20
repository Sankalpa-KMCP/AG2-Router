using System.Net;
using System.Net.Http;
using System.Text;
using AG2Router.AG2.Rpc;
using Xunit;

namespace AG2Router.Tests;

public class MockHttpMessageHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage>? HandlerFunc { get; set; }
    public HttpRequestMessage? LastRequest { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        var response = HandlerFunc != null
            ? HandlerFunc(request)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        return Task.FromResult(response);
    }
}

public class AG2RpcClientTests
{
    [Fact]
    public async Task GetUserStatusAsync_AttachesExpectedHeadersAndPayload()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = req =>
            {
                Assert.Equal(HttpMethod.Post, req.Method);
                Assert.Equal("1", req.Headers.GetValues("Connect-Protocol-Version").FirstOrDefault());
                Assert.Equal("test_csrf_token", req.Headers.GetValues("x-codeium-csrf-token").FirstOrDefault());
                Assert.Equal("application/json", req.Content?.Headers.ContentType?.MediaType);

                var json = "{\"userStatus\":{\"email\":\"user@example.com\",\"name\":\"Test User\"}}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }
        };

        using var client = new AG2RpcClient(mockHandler);
        var result = await client.GetUserStatusAsync(51768, "https", "test_csrf_token");

        Assert.NotNull(result);
        Assert.NotNull(result.UserStatus);
        Assert.Equal("user@example.com", result.UserStatus.Email);
        Assert.Equal("Test User", result.UserStatus.Name);
    }

    [Fact]
    public async Task GetAllCascadeTrajectoriesAsync_ParsesTrajectorySummariesMap()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = _ =>
            {
                var json = "{\"trajectorySummaries\":{\"traj-1\":{\"status\":\"CASCADE_RUN_STATUS_RUNNING\",\"stepCount\":5}}}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }
        };

        using var client = new AG2RpcClient(mockHandler);
        var result = await client.GetAllCascadeTrajectoriesAsync(51768, "https", "test_csrf_token");

        Assert.NotNull(result);
        Assert.NotNull(result.TrajectorySummaries);
        Assert.True(result.TrajectorySummaries.ContainsKey("traj-1"));
        Assert.Equal("CASCADE_RUN_STATUS_RUNNING", result.TrajectorySummaries["traj-1"].Status);
    }

    [Fact]
    public async Task ProbePortAsync_ReturnsTrueFor200And400_ReturnsFalseFor500()
    {
        HttpStatusCode currentStatus = HttpStatusCode.OK;
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = _ => new HttpResponseMessage(currentStatus)
            {
                Content = new StringContent("{}")
            }
        };

        using var client = new AG2RpcClient(mockHandler);

        // 200 OK -> True
        currentStatus = HttpStatusCode.OK;
        Assert.True(await client.ProbePortAsync(51768, "https", "token"));

        // 400 Bad Request -> True (server is active)
        currentStatus = HttpStatusCode.BadRequest;
        Assert.True(await client.ProbePortAsync(51768, "https", "token"));

        // 500 Internal Server Error -> False
        currentStatus = HttpStatusCode.InternalServerError;
        Assert.False(await client.ProbePortAsync(51768, "https", "token"));
    }

    [Fact]
    public async Task CallRpcAsync_ErrorSnippetRedactsEmbeddedSecrets()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("Fatal error with x-codeium-csrf-token: secret_123456789")
            }
        };

        using var client = new AG2RpcClient(mockHandler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetUserStatusAsync(51768, "https", "secret_123456789"));

        Assert.DoesNotContain("secret_123456789", ex.Message);
        Assert.Contains("[REDACTED]", ex.Message);
    }

    [Fact]
    public async Task GetUserStatusAsync_HandlesStringEncoded64BitIntegers()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = _ =>
            {
                var json = "{\"userStatus\":{\"email\":\"user@example.com\",\"planStatus\":{\"availablePromptCredits\":\"500\",\"planInfo\":{\"monthlyPromptCredits\":\"50000\"}}}}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }
        };

        using var client = new AG2RpcClient(mockHandler);
        var result = await client.GetUserStatusAsync(51768, "https", "token");

        Assert.NotNull(result?.UserStatus?.PlanStatus);
        Assert.Equal(500, result.UserStatus.PlanStatus.AvailablePromptCredits);
        Assert.Equal(50000, result.UserStatus.PlanStatus.PlanInfo?.MonthlyPromptCredits);
    }
}
