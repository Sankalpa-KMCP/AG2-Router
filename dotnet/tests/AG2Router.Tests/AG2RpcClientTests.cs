using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using AG2Router.AG2.Rpc;
using Xunit;

namespace AG2Router.Tests;

public class MockHttpMessageHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage>? HandlerFunc { get; set; }
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? AsyncHandlerFunc { get; set; }
    public HttpRequestMessage? LastRequest { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastRequest = request;
        if (AsyncHandlerFunc != null)
        {
            return AsyncHandlerFunc(request, cancellationToken);
        }
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

    [Fact]
    public async Task ProbePortAsync_CompletedEmptyBody400_ReturnsTrue()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(string.Empty)
            }
        };

        using var client = new AG2RpcClient(mockHandler);
        Assert.True(await client.ProbePortAsync(51768, "https", "token"));
    }

    [Fact]
    public async Task ProbePortAsync_ReadExceptionAfterHeaders_ReturnsFalse()
    {
        var throwingStream = new ThrowingStream([], new IOException("Connection forcibly closed by remote host"));
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StreamContent(throwingStream)
            }
        };

        using var client = new AG2RpcClient(mockHandler);
        Assert.False(await client.ProbePortAsync(51768, "https", "token"));
    }

    [Fact]
    public async Task ProbePortAsync_ReadExceptionAfterPartialMismatchContent_ReturnsFalse()
    {
        byte[] partialMismatch = Encoding.UTF8.GetBytes("Client sent an HTTP request to ");
        var throwingStream = new ThrowingStream(partialMismatch, new IOException("Socket aborted mid-transmission"));
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StreamContent(throwingStream)
            }
        };

        using var client = new AG2RpcClient(mockHandler);
        Assert.False(await client.ProbePortAsync(51768, "https", "token"));
    }

    [Fact]
    public async Task ProbePortAsync_MidBodyCancellation_ReturnsFalse()
    {
        var throwingStream = new ThrowingStream([], new OperationCanceledException("Probe token expired"));
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StreamContent(throwingStream)
            }
        };

        using var client = new AG2RpcClient(mockHandler);
        Assert.False(await client.ProbePortAsync(51768, "https", "token"));
    }

    [Fact]
    public async Task ProbePortAsync_Oversized400Response_ReadsAtMost512ApplicationBytes()
    {
        byte[] payload100K = new byte[100_000];
        Array.Fill(payload100K, (byte)'A');
        var countingStream = new CountingStream(new MemoryStream(payload100K));

        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StreamContent(countingStream)
            }
        };

        using var client = new AG2RpcClient(mockHandler);
        bool result = await client.ProbePortAsync(51768, "https", "token");

        Assert.True(result, "Generic harmless 400 must still succeed");
        Assert.Equal(512, countingStream.TotalBytesRead);
    }

    [Theory]
    [InlineData(512, false, true)]   // Exactly 512 bytes harmless -> True
    [InlineData(513, false, true)]   // 513 bytes harmless -> True (stops at 512)
    [InlineData(512, true, false)]   // Exactly 512 bytes with mismatch -> False
    public async Task ProbePortAsync_BoundaryCases_512And513Bytes(int bodyLength, bool includeMismatch, bool expectedResult)
    {
        byte[] bodyBytes = new byte[bodyLength];
        Array.Fill(bodyBytes, (byte)'X');

        if (includeMismatch)
        {
            byte[] mismatchBytes = Encoding.UTF8.GetBytes("Client sent an HTTP request to an HTTPS server.");
            mismatchBytes.CopyTo(bodyBytes, 0);
        }

        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new ByteArrayContent(bodyBytes)
            }
        };

        using var client = new AG2RpcClient(mockHandler);
        bool result = await client.ProbePortAsync(51768, "https", "token");
        Assert.Equal(expectedResult, result);
    }

    [Fact]
    public async Task ProbePortDetailedAsync_ClassifiesFailuresAccurately()
    {
        var mockHandler = new MockHttpMessageHandler();
        using var client = new AG2RpcClient(mockHandler);

        // 1. Success 200
        mockHandler.HandlerFunc = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        var res200 = await client.ProbePortDetailedAsync(51768, "https", "token");
        Assert.True(res200.Success);
        Assert.Equal(ProbeFailureReason.None, res200.FailureReason);
        Assert.Equal(200, res200.StatusCode);

        // 2. Protocol mismatch 400
        mockHandler.HandlerFunc = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("Client sent an HTTP request to an HTTPS server.\n")
        };
        var resMismatch = await client.ProbePortDetailedAsync(51768, "https", "token");
        Assert.False(resMismatch.Success);
        Assert.Equal(ProbeFailureReason.ProtocolMismatch, resMismatch.FailureReason);

        // 3. Server error 500
        mockHandler.HandlerFunc = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{}")
        };
        var res500 = await client.ProbePortDetailedAsync(51768, "https", "token");
        Assert.False(res500.Success);
        Assert.Equal(ProbeFailureReason.ServerError, res500.FailureReason);

        // 4. Redirect 302
        mockHandler.HandlerFunc = _ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("http://127.0.0.1:9999") }
        };
        var res302 = await client.ProbePortDetailedAsync(51768, "https", "token");
        Assert.False(res302.Success);
        Assert.Equal(ProbeFailureReason.RejectedRedirect, res302.FailureReason);

        // 5. Incomplete body on 400
        var throwingStream = new ThrowingStream([], new IOException("Network drop"));
        mockHandler.HandlerFunc = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StreamContent(throwingStream)
        };
        var resIncomplete = await client.ProbePortDetailedAsync(51768, "https", "token");
        Assert.False(resIncomplete.Success);
        Assert.Equal(ProbeFailureReason.IncompleteResponseBody, resIncomplete.FailureReason);
    }

    // =========================================================================
    // PROMPT #088: Constructor Probe Timeout Validation Tests
    // =========================================================================

    [Fact]
    public void Constructor_Default_RetainsThreeSecondProbeDeadline()
    {
        using var client = new AG2RpcClient();
        Assert.Equal(TimeSpan.FromSeconds(3), client.ProbeTimeout);
        Assert.Equal(AG2RpcClient.DefaultProbeTimeout, client.ProbeTimeout);
    }

    [Fact]
    public void Constructor_NullProbeTimeout_PreservesThreeSecondDefault()
    {
        using var client = new AG2RpcClient(probeTimeout: null);
        Assert.Equal(TimeSpan.FromSeconds(3), client.ProbeTimeout);
        Assert.Equal(AG2RpcClient.DefaultProbeTimeout, client.ProbeTimeout);
    }

    [Theory]
    [InlineData(1)]        // 1 ms
    [InlineData(50)]       // 50 ms
    [InlineData(1000)]     // 1 s
    [InlineData(3000)]     // Exactly 3 s (DefaultProbeTimeout)
    public void Constructor_PositiveFiniteOverrideWithinBound_IsAccepted(int milliseconds)
    {
        var timeout = TimeSpan.FromMilliseconds(milliseconds);
        using var client = new AG2RpcClient(probeTimeout: timeout);
        Assert.Equal(timeout, client.ProbeTimeout);
    }

    [Fact]
    public void Constructor_InfiniteTimeout_IsRejected()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new AG2RpcClient(probeTimeout: Timeout.InfiniteTimeSpan));
        Assert.Equal("probeTimeout", ex.ParamName);
    }

    [Fact]
    public void Constructor_ZeroTimeout_IsRejected()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new AG2RpcClient(probeTimeout: TimeSpan.Zero));
        Assert.Equal("probeTimeout", ex.ParamName);
    }

    [Theory]
    [InlineData(-100)]
    [InlineData(-1)]
    [InlineData(-5000)]
    public void Constructor_NegativeTimeout_IsRejected(int milliseconds)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new AG2RpcClient(probeTimeout: TimeSpan.FromMilliseconds(milliseconds)));
        Assert.Equal("probeTimeout", ex.ParamName);
    }

    [Fact]
    public void Constructor_ExceedingDefaultProbeTimeout_IsRejected()
    {
        // 3001 ms (> 3s)
        var ex3001 = Assert.Throws<ArgumentOutOfRangeException>(() => new AG2RpcClient(probeTimeout: TimeSpan.FromMilliseconds(3001)));
        Assert.Equal("probeTimeout", ex3001.ParamName);

        // 10 seconds
        var ex10s = Assert.Throws<ArgumentOutOfRangeException>(() => new AG2RpcClient(probeTimeout: TimeSpan.FromSeconds(10)));
        Assert.Equal("probeTimeout", ex10s.ParamName);

        // TimeSpan.MaxValue
        var exMax = Assert.Throws<ArgumentOutOfRangeException>(() => new AG2RpcClient(probeTimeout: TimeSpan.MaxValue));
        Assert.Equal("probeTimeout", exMax.ParamName);
    }

    [Fact]
    public async Task ProbePortDetailedAsync_Synthetic400WithholdingBody_CannotWaitIndefinitelyUnderAcceptedConfiguration()
    {
        var pendingStream = new PendingReadStream();
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StreamContent(pendingStream)
            }
        };

        // Use valid accepted short timeout of 50ms
        using var client = new AG2RpcClient(mockHandler, probeTimeout: TimeSpan.FromMilliseconds(50));
        var sw = Stopwatch.StartNew();
        var result = await client.ProbePortDetailedAsync(51768, "https", "token", CancellationToken.None);
        sw.Stop();

        Assert.False(result.Success);
        Assert.Equal(ProbeFailureReason.ProbeTimeout, result.FailureReason);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"Probe must complete promptly under configured deadline, elapsed {sw.ElapsedMilliseconds}ms");

        bool boolResult = await client.ProbePortAsync(51768, "https", "token", CancellationToken.None);
        Assert.False(boolResult);
    }

    // =========================================================================
    // Finding 1: Cancellation & Internal Deadline Classification Tests
    // =========================================================================

    [Fact]
    public async Task ProbePortDetailedAsync_CallerTokenCancelledBeforeDispatch_ReturnsCancelled()
    {
        var mockHandler = new MockHttpMessageHandler();
        using var client = new AG2RpcClient(mockHandler);

        using var callerCts = new CancellationTokenSource();
        callerCts.Cancel();

        var detailedResult = await client.ProbePortDetailedAsync(51768, "https", "token", callerCts.Token);
        Assert.False(detailedResult.Success);
        Assert.Equal(ProbeFailureReason.Cancelled, detailedResult.FailureReason);

        bool boolResult = await client.ProbePortAsync(51768, "https", "token", callerCts.Token);
        Assert.False(boolResult);
    }

    [Fact]
    public async Task ProbePortDetailedAsync_CallerTokenCancelledDuringPending400Body_ReturnsCancelled()
    {
        var pendingStream = new PendingReadStream();
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StreamContent(pendingStream)
            }
        };
        using var client = new AG2RpcClient(mockHandler);
        using var callerCts = new CancellationTokenSource();

        var probeTask = client.ProbePortDetailedAsync(51768, "https", "token", callerCts.Token);
        await pendingStream.ReadStarted;

        callerCts.Cancel();

        var result = await probeTask;
        Assert.False(result.Success);
        Assert.Equal(ProbeFailureReason.Cancelled, result.FailureReason);

        // Also test Boolean ProbePortAsync parity
        var pendingStream2 = new PendingReadStream();
        mockHandler.HandlerFunc = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StreamContent(pendingStream2)
        };
        using var callerCts2 = new CancellationTokenSource();
        var boolTask = client.ProbePortAsync(51768, "https", "token", callerCts2.Token);
        await pendingStream2.ReadStarted;
        callerCts2.Cancel();
        bool boolResult = await boolTask;
        Assert.False(boolResult);
    }

    [Fact]
    public async Task ProbePortDetailedAsync_InternalDeadlineExpiresDuringDispatch_ReturnsProbeTimeout()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            AsyncHandlerFunc = async (_, ct) =>
            {
                await Task.Delay(300, ct);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }
        };

        using var client = new AG2RpcClient(mockHandler, probeTimeout: TimeSpan.FromMilliseconds(50));
        var result = await client.ProbePortDetailedAsync(51768, "https", "token", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ProbeFailureReason.ProbeTimeout, result.FailureReason);

        bool boolResult = await client.ProbePortAsync(51768, "https", "token", CancellationToken.None);
        Assert.False(boolResult);
    }

    [Fact]
    public async Task ProbePortDetailedAsync_InternalDeadlineExpiresDuring400BodyRead_ReturnsProbeTimeout()
    {
        var pendingStream = new PendingReadStream();
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StreamContent(pendingStream)
            }
        };

        using var client = new AG2RpcClient(mockHandler, probeTimeout: TimeSpan.FromMilliseconds(50));
        var result = await client.ProbePortDetailedAsync(51768, "https", "token", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ProbeFailureReason.ProbeTimeout, result.FailureReason);

        bool boolResult = await client.ProbePortAsync(51768, "https", "token", CancellationToken.None);
        Assert.False(boolResult);
    }

    [Fact]
    public async Task ProbePortDetailedAsync_UnexpectedCancellationDuringDispatch_ReturnsUnknownWithoutFabricatingTimeout()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = _ => throw new OperationCanceledException("Unexpected external cancellation without token")
        };

        using var client = new AG2RpcClient(mockHandler);
        var result = await client.ProbePortDetailedAsync(51768, "https", "token", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ProbeFailureReason.Unknown, result.FailureReason);

        bool boolResult = await client.ProbePortAsync(51768, "https", "token", CancellationToken.None);
        Assert.False(boolResult);
    }

    [Fact]
    public async Task ProbePortDetailedAsync_UnexpectedCancellationDuring400Body_ReturnsUnknownWithoutFabricatingTimeout()
    {
        var throwingStream = new ThrowingStream([], new OperationCanceledException("Unexpected stream cancellation"));
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StreamContent(throwingStream)
            }
        };

        using var client = new AG2RpcClient(mockHandler);
        var result = await client.ProbePortDetailedAsync(51768, "https", "token", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ProbeFailureReason.Unknown, result.FailureReason);

        bool boolResult = await client.ProbePortAsync(51768, "https", "token", CancellationToken.None);
        Assert.False(boolResult);
    }

    [Fact]
    public async Task ProbePortAsync_DetailedAndBooleanAgreeOnAllOutcomes()
    {
        var mockHandler = new MockHttpMessageHandler();
        using var client = new AG2RpcClient(mockHandler);

        // Case 1: 200 OK -> both true
        mockHandler.HandlerFunc = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        var detailed1 = await client.ProbePortDetailedAsync(51768, "https", "token");
        var bool1 = await client.ProbePortAsync(51768, "https", "token");
        Assert.True(detailed1.Success);
        Assert.True(bool1);
        Assert.Equal(detailed1.Success, bool1);

        // Case 2: Generic 400 (no mismatch) -> both true
        mockHandler.HandlerFunc = _ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"bad csrf\"}") };
        var detailed2 = await client.ProbePortDetailedAsync(51768, "https", "token");
        var bool2 = await client.ProbePortAsync(51768, "https", "token");
        Assert.True(detailed2.Success);
        Assert.True(bool2);
        Assert.Equal(detailed2.Success, bool2);

        // Case 3: 400 Protocol Mismatch -> both false
        mockHandler.HandlerFunc = _ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("Client sent an HTTP request to an HTTPS server.\n") };
        var detailed3 = await client.ProbePortDetailedAsync(51768, "https", "token");
        var bool3 = await client.ProbePortAsync(51768, "https", "token");
        Assert.False(detailed3.Success);
        Assert.False(bool3);
        Assert.Equal(detailed3.Success, bool3);

        // Case 4: 500 Server Error -> both false
        mockHandler.HandlerFunc = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var detailed4 = await client.ProbePortDetailedAsync(51768, "https", "token");
        var bool4 = await client.ProbePortAsync(51768, "https", "token");
        Assert.False(detailed4.Success);
        Assert.False(bool4);
        Assert.Equal(detailed4.Success, bool4);
    }

    // =========================================================================
    // Finding 2: Default Interface Method Adapter Tests
    // =========================================================================

    [Fact]
    public async Task DefaultInterfaceAdapter_TrueCompletedTask_ReturnsSuccessNone()
    {
        IAG2RpcClient client = new BooleanOnlyRpcClient((_, _, _, _) => Task.FromResult(true));

        var result = await client.ProbePortDetailedAsync(51768, "https", "token");

        Assert.True(result.Success);
        Assert.Equal(ProbeFailureReason.None, result.FailureReason);
        Assert.Equal(0, result.StatusCode);
    }

    [Fact]
    public async Task DefaultInterfaceAdapter_FalseCompletedTask_ReturnsUnknownFailure()
    {
        IAG2RpcClient client = new BooleanOnlyRpcClient((_, _, _, _) => Task.FromResult(false));

        var result = await client.ProbePortDetailedAsync(51768, "https", "token");

        Assert.False(result.Success);
        Assert.Equal(ProbeFailureReason.Unknown, result.FailureReason);
        Assert.Equal(0, result.StatusCode);
    }

    [Fact]
    public async Task DefaultInterfaceAdapter_CanceledTask_PreservesCancellationSemanticsWithoutAggregateException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var tcs = new TaskCompletionSource<bool>();
        tcs.SetCanceled(cts.Token);

        IAG2RpcClient client = new BooleanOnlyRpcClient((_, _, _, _) => tcs.Task);

        var task = client.ProbePortDetailedAsync(51768, "https", "token", cts.Token);

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.IsNotType<AggregateException>(ex);
        Assert.True(task.IsCanceled);
        Assert.False(task.IsFaulted);
    }

    [Fact]
    public async Task DefaultInterfaceAdapter_FaultedTask_PreservesFaultSemanticsWithoutAggregateException()
    {
        var expectedEx = new InvalidOperationException("Custom probe internal fault");
        IAG2RpcClient client = new BooleanOnlyRpcClient((_, _, _, _) => Task.FromException<bool>(expectedEx));

        var task = client.ProbePortDetailedAsync(51768, "https", "token");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.Same(expectedEx, ex);
        Assert.IsNotType<AggregateException>(ex);
        Assert.True(task.IsFaulted);
        Assert.False(task.IsCanceled);
    }

    [Fact]
    public async Task DefaultInterfaceAdapter_ProductionImplementationParity()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            HandlerFunc = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }
        };
        IAG2RpcClient prodClient = new AG2RpcClient(mockHandler);

        // Production client overrides ProbePortDetailedAsync directly
        var detailedResult = await prodClient.ProbePortDetailedAsync(51768, "https", "token");
        bool boolResult = await prodClient.ProbePortAsync(51768, "https", "token");

        Assert.True(detailedResult.Success);
        Assert.True(boolResult);
        Assert.Equal(ProbeFailureReason.None, detailedResult.FailureReason);
        Assert.Equal(200, detailedResult.StatusCode);
    }

    private sealed class BooleanOnlyRpcClient(Func<int, string, string, CancellationToken, Task<bool>> probeFunc) : IAG2RpcClient
    {
        public Task<bool> ProbePortAsync(int port, string protocol, string csrfToken, CancellationToken cancellationToken = default)
            => probeFunc(port, protocol, csrfToken, cancellationToken);

        public Task<RawUserStatusResponse?> GetUserStatusAsync(int port, string protocol, string csrfToken, CancellationToken cancellationToken = default)
            => Task.FromResult<RawUserStatusResponse?>(null);

        public Task<RawTrajectoriesResponse?> GetAllCascadeTrajectoriesAsync(int port, string protocol, string csrfToken, CancellationToken cancellationToken = default)
            => Task.FromResult<RawTrajectoriesResponse?>(null);
    }

    private sealed class PendingReadStream : Stream
    {
        private readonly TaskCompletionSource _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ReadStarted => _readStarted.Task;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 100;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _readStarted.TrySetResult();
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return await tcs.Task;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            _readStarted.TrySetResult();
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return await tcs.Task;
        }
    }

    private sealed class CountingStream(Stream innerStream) : Stream
    {
        public int TotalBytesRead { get; private set; }

        public override bool CanRead => innerStream.CanRead;
        public override bool CanSeek => innerStream.CanSeek;
        public override bool CanWrite => innerStream.CanWrite;
        public override long Length => innerStream.Length;
        public override long Position { get => innerStream.Position; set => innerStream.Position = value; }
        public override void Flush() => innerStream.Flush();
        public override long Seek(long offset, SeekOrigin origin) => innerStream.Seek(offset, origin);
        public override void SetLength(long value) => innerStream.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => innerStream.Write(buffer, offset, count);

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = innerStream.Read(buffer, offset, count);
            TotalBytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await innerStream.ReadAsync(buffer, cancellationToken);
            TotalBytesRead += read;
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int read = await innerStream.ReadAsync(buffer, offset, count, cancellationToken);
            TotalBytesRead += read;
            return read;
        }
    }

    private sealed class ThrowingStream(byte[] initialBytes, Exception exceptionToThrow) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => initialBytes.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < initialBytes.Length)
            {
                int toCopy = Math.Min(buffer.Length, initialBytes.Length - _position);
                initialBytes.AsSpan(_position, toCopy).CopyTo(buffer.Span);
                _position += toCopy;
                return ValueTask.FromResult(toCopy);
            }

            throw exceptionToThrow;
        }
    }
}
