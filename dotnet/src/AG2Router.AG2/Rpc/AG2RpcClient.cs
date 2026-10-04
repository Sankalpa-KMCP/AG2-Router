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
    private const string StandardMetadataJson = "{\"metadata\":{\"ideName\":\"antigravity\",\"extensionName\":\"antigravity\"}}";
    private const string ServicePrefix = "/exa.language_server_pb.LanguageServerService";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public AG2RpcClient(HttpMessageHandler? customHandler = null)
    {
        if (customHandler != null)
        {
            _httpClient = new HttpClient(customHandler, disposeHandler: false);
            _ownsHttpClient = true;
        }
        else
        {
            var handler = new SocketsHttpHandler
            {
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
            _httpClient = new HttpClient(handler, disposeHandler: true);
            _ownsHttpClient = true;
        }

        _httpClient.Timeout = TimeSpan.FromSeconds(5);
    }

    public async Task<bool> ProbePortAsync(int port, string protocol, string csrfToken, CancellationToken cancellationToken = default)
    {
        var url = $"{protocol}://127.0.0.1:{port}{ServicePrefix}/GetUserStatus";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(3));

        try
        {
            using var request = CreateConnectRpcRequest(HttpMethod.Post, url, csrfToken, StandardMetadataJson);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);

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
        var url = $"{protocol}://127.0.0.1:{port}{ServicePrefix}/GetUserStatus";
        return await CallRpcAsync<RawUserStatusResponse>(url, csrfToken, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RawTrajectoriesResponse?> GetAllCascadeTrajectoriesAsync(int port, string protocol, string csrfToken, CancellationToken cancellationToken = default)
    {
        var url = $"{protocol}://127.0.0.1:{port}{ServicePrefix}/GetAllCascadeTrajectories";
        return await CallRpcAsync<RawTrajectoriesResponse>(url, csrfToken, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RawGeneratorMetadataEntry>> GetCascadeTrajectoryGeneratorMetadataAsync(
        int port, string protocol, string csrfToken, string cascadeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cascadeId);
        var url = $"{protocol}://127.0.0.1:{port}{ServicePrefix}/GetCascadeTrajectoryGeneratorMetadata";
        string requestBody = JsonSerializer.Serialize(new GeneratorMetadataRequest { CascadeId = cascadeId });

        using var request = CreateConnectRpcRequest(HttpMethod.Post, url, csrfToken, requestBody);
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"RPC transport error: {AG2Security.SanitizeError(ex)}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // Body is intentionally not read or logged: generator metadata can embed
                // prompt-adjacent content. Only the status code reaches diagnostics.
                throw new HttpRequestException(
                    $"Connect-RPC call failed with HTTP {(int)response.StatusCode}.", null, response.StatusCode);
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return UsageRpcPayloads.ExtractEntries(JsonDocument.Parse(content));
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("Failed to deserialize Connect-RPC JSON payload.", ex);
            }
        }
    }

    private async Task<T?> CallRpcAsync<T>(string url, string csrfToken, CancellationToken cancellationToken) where T : class
    {
        using var request = CreateConnectRpcRequest(HttpMethod.Post, url, csrfToken, StandardMetadataJson);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"RPC transport error: {AG2Security.SanitizeError(ex)}", ex);
        }

        using (response)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

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
    }

    private static HttpRequestMessage CreateConnectRpcRequest(HttpMethod method, string url, string csrfToken, string jsonBody)
    {
        var request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
        };

        request.Headers.Add("Connect-Protocol-Version", "1");
        request.Headers.Add("x-codeium-csrf-token", csrfToken);

        return request;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
