using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Text;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AG2Router.AG2.Security;

namespace AG2Router.AG2.Rpc;

/// <summary>
/// Native C# client for Antigravity 2 Connect-RPC service over loopback.
/// Implements unary Connect-RPC JSON protocol version 1 with loopback-scoped TLS bypass.
/// </summary>
public class AG2RpcClient : IAG2RpcClient, IUsageRpcClient, IDisposable
{
    public static readonly TimeSpan DefaultRpcTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(3);

    private const string StandardMetadataJson = "{\"metadata\":{\"ideName\":\"antigravity\",\"extensionName\":\"antigravity\"}}";
    private const string ServicePrefix = "/exa.language_server_pb.LanguageServerService";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly TimeSpan _rpcTimeout;

    public TimeSpan RpcTimeout => _rpcTimeout;

    public AG2RpcClient(HttpMessageHandler? customHandler = null, TimeSpan? rpcTimeout = null)
    {
        _rpcTimeout = rpcTimeout ?? DefaultRpcTimeout;

        if (customHandler != null)
        {
            _httpClient = new HttpClient(customHandler, disposeHandler: false);
            _ownsHttpClient = true;
        }
        else
        {
            var handler = CreateDefaultHandler();
            _httpClient = new HttpClient(handler, disposeHandler: true);
            _ownsHttpClient = true;
        }

        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    /// <summary>
    /// Creates the default production SocketsHttpHandler for Connect-RPC daemon communication.
    /// Automatic redirect following is strictly disabled to prevent credential/token leakage (B2).
    /// TLS certificate validation allows self-signed certificates strictly for loopback hosts.
    /// </summary>
    public static SocketsHttpHandler CreateDefaultHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(3),
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (sender, cert, chain, sslPolicyErrors) =>
            {
                // Safely bypass self-signed certificate strictly for loopback daemon
                if (sender is SslStream sslStream && sslStream.TargetHostName is { } host)
                {
                    if (host is "127.0.0.1" or "localhost" or "::1")
                    {
                        return true;
                    }
                }
                return sslPolicyErrors == SslPolicyErrors.None;
            }
        }
    };

    public async Task<bool> ProbePortAsync(int port, string protocol, string csrfToken, CancellationToken cancellationToken = default)
    {
        string url;
        try
        {
            url = BuildEndpointUrl(port, protocol, "GetUserStatus");
        }
        catch (ArgumentException)
        {
            return false;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(DefaultProbeTimeout);

        try
        {
            using var request = CreateConnectRpcRequest(HttpMethod.Post, url, csrfToken, StandardMetadataJson);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);

            // Redirect responses (3xx) are rejected as invalid daemon responses to prevent credential forwarding
            if (IsRedirectStatusCode(response.StatusCode))
            {
                return false;
            }

            // Active server responds with HTTP status < 500 (even 4xx indicates an active Connect-RPC endpoint)
            return (int)response.StatusCode < 500;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<RawUserStatusResponse?> GetUserStatusAsync(int port, string protocol, string csrfToken, CancellationToken cancellationToken = default)
    {
        var url = BuildEndpointUrl(port, protocol, "GetUserStatus");
        return await CallRpcAsync<RawUserStatusResponse>(url, csrfToken, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RawTrajectoriesResponse?> GetAllCascadeTrajectoriesAsync(int port, string protocol, string csrfToken, CancellationToken cancellationToken = default)
    {
        var url = BuildEndpointUrl(port, protocol, "GetAllCascadeTrajectories");
        return await CallRpcAsync<RawTrajectoriesResponse>(url, csrfToken, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RawGeneratorMetadataEntry>> GetCascadeTrajectoryGeneratorMetadataAsync(
        int port, string protocol, string csrfToken, string cascadeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cascadeId);
        var url = BuildEndpointUrl(port, protocol, "GetCascadeTrajectoryGeneratorMetadata");
        string requestBody = JsonSerializer.Serialize(new GeneratorMetadataRequest { CascadeId = cascadeId });

        using var deadlineCts = CreateDeadlineCts(cancellationToken);
        using var request = CreateConnectRpcRequest(HttpMethod.Post, url, csrfToken, requestBody);

        using var response = await SendWithDeadlineAsync(request, deadlineCts, cancellationToken).ConfigureAwait(false);

        if (IsRedirectStatusCode(response.StatusCode))
        {
            throw new HttpRequestException(
                $"Connect-RPC call failed: server returned redirect HTTP {(int)response.StatusCode}. Automatic and manual redirects are rejected to prevent credential forwarding.",
                null,
                response.StatusCode);
        }

        if (!response.IsSuccessStatusCode)
        {
            // Body is intentionally not read or logged: generator metadata can embed
            // prompt-adjacent content. Only the status code reaches diagnostics.
            throw new HttpRequestException(
                $"Connect-RPC call failed with HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        }

        var content = await ReadContentWithDeadlineAsync(response.Content, deadlineCts, cancellationToken).ConfigureAwait(false);
        try
        {
            return UsageRpcPayloads.ExtractEntries(JsonDocument.Parse(content));
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Failed to deserialize Connect-RPC JSON payload.", ex);
        }
    }

    private async Task<T?> CallRpcAsync<T>(string url, string csrfToken, CancellationToken cancellationToken) where T : class
    {
        using var deadlineCts = CreateDeadlineCts(cancellationToken);
        using var request = CreateConnectRpcRequest(HttpMethod.Post, url, csrfToken, StandardMetadataJson);

        using var response = await SendWithDeadlineAsync(request, deadlineCts, cancellationToken).ConfigureAwait(false);

        if (IsRedirectStatusCode(response.StatusCode))
        {
            throw new HttpRequestException(
                $"Connect-RPC call failed: server returned redirect HTTP {(int)response.StatusCode}. Automatic and manual redirects are rejected to prevent credential forwarding.",
                null,
                response.StatusCode);
        }

        var content = await ReadContentWithDeadlineAsync(response.Content, deadlineCts, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var sanitizedSnippet = AG2Security.RedactSensitiveText(content);
            if (sanitizedSnippet.Length > 200)
            {
                sanitizedSnippet = sanitizedSnippet[..200] + "...";
            }
            throw new HttpRequestException($"Connect-RPC call failed with HTTP {(int)response.StatusCode}: {sanitizedSnippet}", null, response.StatusCode);
        }

        try
        {
            return JsonSerializer.Deserialize<T>(content, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to deserialize Connect-RPC JSON payload: {ex.Message}", ex);
        }
    }

    private CancellationTokenSource CreateDeadlineCts(CancellationToken callerToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        cts.CancelAfter(_rpcTimeout);
        return cts;
    }

    private async Task<HttpResponseMessage> SendWithDeadlineAsync(
        HttpRequestMessage request, CancellationTokenSource deadlineCts, CancellationToken callerToken)
    {
        try
        {
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadlineCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new TaskCanceledException(
                $"Connect-RPC call timed out after {_rpcTimeout.TotalSeconds.ToString("G", CultureInfo.InvariantCulture)} seconds.",
                new TimeoutException("The RPC operation timed out.", ex));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"RPC transport error: {AG2Security.SanitizeError(ex)}", ex);
        }
    }

    private async Task<string> ReadContentWithDeadlineAsync(
        HttpContent content, CancellationTokenSource deadlineCts, CancellationToken callerToken)
    {
        try
        {
            return await content.ReadAsStringAsync(deadlineCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new TaskCanceledException(
                $"Connect-RPC call timed out after {_rpcTimeout.TotalSeconds.ToString("G", CultureInfo.InvariantCulture)} seconds.",
                new TimeoutException("The RPC operation timed out.", ex));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"RPC transport error: {AG2Security.SanitizeError(ex)}", ex);
        }
    }

    private static HttpRequestMessage CreateConnectRpcRequest(HttpMethod method, string url, string csrfToken, string jsonBody)
    {
        var uri = new Uri(url, UriKind.Absolute);
        if (uri.Host is not ("127.0.0.1" or "localhost" or "::1") || !uri.IsLoopback)
        {
            throw new InvalidOperationException(
                $"Token-bearing Connect-RPC request rejected: destination host '{uri.Host}' is not a verified loopback daemon endpoint.");
        }

        var request = new HttpRequestMessage(method, uri)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
        };

        request.Headers.Add("Connect-Protocol-Version", "1");
        request.Headers.Add("x-codeium-csrf-token", csrfToken);

        return request;
    }

    private static string BuildEndpointUrl(int port, string protocol, string rpcMethod)
    {
        if (port is <= 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be a valid TCP port (1-65535).");
        }

        string normalizedProtocol = protocol?.ToLowerInvariant() switch
        {
            "http" => "http",
            "https" => "https",
            _ => throw new ArgumentException("Protocol must be 'http' or 'https'.", nameof(protocol))
        };

        return $"{normalizedProtocol}://127.0.0.1:{port}{ServicePrefix}/{rpcMethod}";
    }

    private static bool IsRedirectStatusCode(HttpStatusCode statusCode) =>
        (int)statusCode is >= 300 and < 400;

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
