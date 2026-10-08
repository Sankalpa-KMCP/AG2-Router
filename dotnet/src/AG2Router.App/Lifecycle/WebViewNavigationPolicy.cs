namespace AG2Router.App.Lifecycle;

/// <summary>
/// Enforces origin boundaries for WebView2 top-level navigation.
/// Ensures the native dashboard window only navigates to the trusted local application origin,
/// cancelling unexpected navigations to foreign origins, untrusted schemes, or external ports.
/// </summary>
public sealed class WebViewNavigationPolicy
{
    public Uri TrustedOrigin { get; }
    public string TrustedScheme => TrustedOrigin.Scheme;
    public string TrustedHost => TrustedOrigin.Host;
    public int TrustedPort => TrustedOrigin.Port;

    public WebViewNavigationPolicy(string dashboardUrl)
    {
        if (string.IsNullOrWhiteSpace(dashboardUrl))
            throw new ArgumentException("Dashboard URL cannot be null or empty.", nameof(dashboardUrl));

        if (!Uri.TryCreate(dashboardUrl, UriKind.Absolute, out var uri))
            throw new ArgumentException($"Dashboard URL '{dashboardUrl}' is not a valid absolute URI.", nameof(dashboardUrl));

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Dashboard URL scheme must be HTTP, found '{uri.Scheme}'.", nameof(dashboardUrl));

        if (!IsAllowedLoopbackHost(uri.Host))
            throw new ArgumentException($"Dashboard URL host must be an allowed loopback host (127.0.0.1 or localhost), found '{uri.Host}'.", nameof(dashboardUrl));

        if (uri.Port <= 0 || uri.Port > 65535)
            throw new ArgumentException($"Dashboard URL port must be a valid positive port number, found '{uri.Port}'.", nameof(dashboardUrl));

        TrustedOrigin = new Uri($"{uri.Scheme}://{uri.Host}:{uri.Port}", UriKind.Absolute);
    }

    public WebViewNavigationPolicy(Uri dashboardUri)
    {
        ArgumentNullException.ThrowIfNull(dashboardUri);

        if (!dashboardUri.IsAbsoluteUri)
            throw new ArgumentException($"Dashboard URI '{dashboardUri}' must be absolute.", nameof(dashboardUri));

        if (!string.Equals(dashboardUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Dashboard URI scheme must be HTTP, found '{dashboardUri.Scheme}'.", nameof(dashboardUri));

        if (!IsAllowedLoopbackHost(dashboardUri.Host))
            throw new ArgumentException($"Dashboard URI host must be an allowed loopback host (127.0.0.1 or localhost), found '{dashboardUri.Host}'.", nameof(dashboardUri));

        if (dashboardUri.Port <= 0 || dashboardUri.Port > 65535)
            throw new ArgumentException($"Dashboard URI port must be a valid positive port number, found '{dashboardUri.Port}'.", nameof(dashboardUri));

        TrustedOrigin = new Uri($"{dashboardUri.Scheme}://{dashboardUri.Host}:{dashboardUri.Port}", UriKind.Absolute);
    }

    public bool IsAllowedNavigation(string? targetUrl) => ShouldAllowNavigation(targetUrl, out _);

    public bool IsAllowedNavigation(Uri? targetUri) => ShouldAllowNavigation(targetUri, out _);

    public bool ShouldAllowNavigation(string? targetUrl, out string? rejectionReason)
    {
        if (string.IsNullOrWhiteSpace(targetUrl))
        {
            rejectionReason = "Target URL is null, empty, or whitespace.";
            return false;
        }

        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var targetUri))
        {
            rejectionReason = $"Target URL '{targetUrl}' is not a valid absolute URI.";
            return false;
        }

        return ShouldAllowNavigation(targetUri, out rejectionReason);
    }

    public bool ShouldAllowNavigation(Uri? targetUri, out string? rejectionReason)
    {
        if (targetUri == null)
        {
            rejectionReason = "Target URI is null.";
            return false;
        }

        if (!targetUri.IsAbsoluteUri)
        {
            rejectionReason = "Target URI is not absolute.";
            return false;
        }

        if (!string.IsNullOrEmpty(targetUri.UserInfo))
        {
            rejectionReason = "Target URI contains userinfo which is prohibited.";
            return false;
        }

        if (!string.Equals(targetUri.Scheme, TrustedScheme, StringComparison.OrdinalIgnoreCase))
        {
            rejectionReason = $"Target scheme '{targetUri.Scheme}' does not match trusted scheme '{TrustedScheme}'.";
            return false;
        }

        if (!string.Equals(targetUri.Host, TrustedHost, StringComparison.OrdinalIgnoreCase))
        {
            rejectionReason = $"Target host '{targetUri.Host}' does not match trusted host '{TrustedHost}'.";
            return false;
        }

        if (targetUri.Port != TrustedPort)
        {
            rejectionReason = $"Target port '{targetUri.Port}' does not match trusted port '{TrustedPort}'.";
            return false;
        }

        rejectionReason = null;
        return true;
    }

    private static bool IsAllowedLoopbackHost(string? host) =>
        string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
}
