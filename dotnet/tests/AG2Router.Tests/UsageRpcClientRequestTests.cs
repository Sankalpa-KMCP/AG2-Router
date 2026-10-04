using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AG2Router.AG2.Rpc;
using Xunit;

namespace AG2Router.Tests;

/// <summary>
/// F-02 regression: the real AG2RpcClient GM request path must emit valid, correctly escaped
/// JSON for every valid cascadeId, with Connect-RPC headers intact and no response-body logging.
/// Exercises the real client over a capturing fake HTTP handler — no live language server.
/// </summary>
public class UsageRpcClientRequestTests
{
    private sealed class CapturingHandler(HttpStatusCode statusCode = HttpStatusCode.OK, string responseBody = "{}") : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static async Task<JsonDocument> SendAndCaptureAsync(string cascadeId, CapturingHandler? handler = null)
    {
        handler ??= new CapturingHandler();
        using var client = new AG2RpcClient(handler);
        var entries = await client.GetCascadeTrajectoryGeneratorMetadataAsync(41001, "https", "csrf-token", cascadeId);
        Assert.Empty(entries);
        handler.LastRequest!.Headers.TryGetValues("Connect-Protocol-Version", out var protocol);
        Assert.Equal(["1"], protocol);
        return JsonDocument.Parse(handler.LastBody!);
    }

    [Fact]
    public async Task OrdinaryCascadeId_EmitsValidSinglePropertyJson()
    {
        using var json = await SendAndCaptureAsync("casc-123");
        var root = json.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        var cascadeProperty = Assert.Single(root.EnumerateObject());
        Assert.Equal("cascadeId", cascadeProperty.Name);
        Assert.Equal("casc-123", cascadeProperty.Value.GetString());
    }

    [Theory]
    [InlineData("quote\"inside")]
    [InlineData("back\\slash")]
    [InlineData("quote\\and\"mixed")]
    [InlineData("unicode-κασκ-会話-🚀")]
    [InlineData("line\nbreak\ttab")]
    public async Task SpecialCharacters_AreEscaped_AndRoundTripExactly(string cascadeId)
    {
        using var json = await SendAndCaptureAsync(cascadeId);
        var property = Assert.Single(json.RootElement.EnumerateObject());
        Assert.Equal("cascadeId", property.Name);
        Assert.Equal(cascadeId, property.Value.GetString());
    }

    [Fact]
    public async Task Request_KeepsConnectRpcHeadersAndJsonContentType()
    {
        var handler = new CapturingHandler();
        using var client = new AG2RpcClient(handler);
        await client.GetCascadeTrajectoryGeneratorMetadataAsync(41001, "https", "csrf-token", "casc-123");

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("https://127.0.0.1:41001/exa.language_server_pb.LanguageServerService/GetCascadeTrajectoryGeneratorMetadata",
            handler.LastRequest.RequestUri!.ToString());
        handler.LastRequest.Content!.Headers.TryGetValues("Content-Type", out var contentType);
        Assert.Equal("application/json; charset=utf-8", Assert.Single(contentType!));
        handler.LastRequest.Headers.TryGetValues("x-codeium-csrf-token", out var csrf);
        Assert.Equal(["csrf-token"], csrf);
    }

    [Fact]
    public async Task ErrorResponses_NeverIncludeResponseBody_InDiagnostics()
    {
        var handler = new CapturingHandler(HttpStatusCode.InternalServerError,
            "{\"error\":\"SECRET-PROMPT-LEAK-SHOULD-NOT-APPEAR\"}");
        using var client = new AG2RpcClient(handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetCascadeTrajectoryGeneratorMetadataAsync(41001, "https", "csrf", "casc-123"));

        Assert.DoesNotContain("SECRET-PROMPT-LEAK-SHOULD-NOT-APPEAR", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TransportFailures_AreSanitized()
    {
        var handler = new FailingHandler();
        using var client = new AG2RpcClient(handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetCascadeTrajectoryGeneratorMetadataAsync(41001, "https", "csrf", "casc-123"));

        Assert.StartsWith("RPC transport error:", exception.Message, StringComparison.Ordinal);
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("socket dropped; token=abc123def"));
    }
}
