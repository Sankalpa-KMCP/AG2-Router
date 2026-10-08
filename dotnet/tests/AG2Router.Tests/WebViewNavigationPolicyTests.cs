using AG2Router.App.Lifecycle;
using Xunit;

namespace AG2Router.Tests;

public class WebViewNavigationPolicyTests
{
    private const string DefaultTrustedUrl = "http://127.0.0.1:54321/index.html";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsNullOrWhitespaceUrl(string? url)
    {
        Assert.Throws<ArgumentException>(() => new WebViewNavigationPolicy(url!));
    }

    [Theory]
    [InlineData("/relative/path")]
    [InlineData("index.html")]
    [InlineData("not a uri")]
    public void Constructor_RejectsNonAbsoluteUri(string url)
    {
        Assert.Throws<ArgumentException>(() => new WebViewNavigationPolicy(url));
    }

    [Theory]
    [InlineData("https://127.0.0.1:54321/index.html")]
    [InlineData("ftp://127.0.0.1:54321/index.html")]
    [InlineData("file:///C:/index.html")]
    [InlineData("ws://127.0.0.1:54321/index.html")]
    public void Constructor_RejectsNonHttpScheme(string url)
    {
        var ex = Assert.Throws<ArgumentException>(() => new WebViewNavigationPolicy(url));
        Assert.Contains("scheme must be HTTP", ex.Message);
    }

    [Theory]
    [InlineData("http://example.com:54321/index.html")]
    [InlineData("http://192.168.1.50:54321/index.html")]
    [InlineData("http://router.local:54321/index.html")]
    [InlineData("http://attacker.com/index.html")]
    public void Constructor_RejectsNonLoopbackHost(string url)
    {
        var ex = Assert.Throws<ArgumentException>(() => new WebViewNavigationPolicy(url));
        Assert.Contains("host must be an allowed loopback host", ex.Message);
    }

    [Fact]
    public void Constructor_AcceptsValidIpv4LoopbackUrl()
    {
        var policy = new WebViewNavigationPolicy("http://127.0.0.1:54321/index.html");
        Assert.Equal("http", policy.TrustedScheme);
        Assert.Equal("127.0.0.1", policy.TrustedHost);
        Assert.Equal(54321, policy.TrustedPort);
        Assert.Equal("http://127.0.0.1:54321/", policy.TrustedOrigin.AbsoluteUri);
    }

    [Fact]
    public void Constructor_AcceptsValidLocalhostUrl()
    {
        var policy = new WebViewNavigationPolicy("http://localhost:54321/index.html");
        Assert.Equal("http", policy.TrustedScheme);
        Assert.Equal("localhost", policy.TrustedHost);
        Assert.Equal(54321, policy.TrustedPort);
        Assert.Equal("http://localhost:54321/", policy.TrustedOrigin.AbsoluteUri);
    }

    [Fact]
    public void Constructor_UriOverload_ValidatesParameters()
    {
        Assert.Throws<ArgumentNullException>(() => new WebViewNavigationPolicy((Uri)null!));
        Assert.Throws<ArgumentException>(() => new WebViewNavigationPolicy(new Uri("/path", UriKind.Relative)));
        Assert.Throws<ArgumentException>(() => new WebViewNavigationPolicy(new Uri("https://127.0.0.1:54321/")));
        Assert.Throws<ArgumentException>(() => new WebViewNavigationPolicy(new Uri("http://evil.com:54321/")));

        var valid = new WebViewNavigationPolicy(new Uri(DefaultTrustedUrl));
        Assert.Equal("127.0.0.1", valid.TrustedHost);
        Assert.Equal(54321, valid.TrustedPort);
    }

    [Theory]
    [InlineData("http://127.0.0.1:54321/index.html")]
    [InlineData("http://127.0.0.1:54321/")]
    [InlineData("http://127.0.0.1:54321/styles.css")]
    [InlineData("http://127.0.0.1:54321/app.js")]
    [InlineData("http://127.0.0.1:54321/api/status")]
    [InlineData("http://127.0.0.1:54321/index.html#settings")]
    [InlineData("http://127.0.0.1:54321/index.html#activity")]
    [InlineData("http://127.0.0.1:54321/index.html?tab=usage")]
    [InlineData("HTTP://127.0.0.1:54321/INDEX.HTML")]
    public void IsAllowedNavigation_AllowsSameOriginNavigations(string targetUrl)
    {
        var policy = new WebViewNavigationPolicy(DefaultTrustedUrl);
        Assert.True(policy.IsAllowedNavigation(targetUrl));
        Assert.True(policy.ShouldAllowNavigation(targetUrl, out var reason));
        Assert.Null(reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/index.html")]
    [InlineData("relative/path")]
    [InlineData("http://[invalid-host")]
    public void IsAllowedNavigation_BlocksNullEmptyOrMalformedUrls(string? targetUrl)
    {
        var policy = new WebViewNavigationPolicy(DefaultTrustedUrl);
        Assert.False(policy.IsAllowedNavigation(targetUrl));
        Assert.False(policy.ShouldAllowNavigation(targetUrl, out var reason));
        Assert.NotNull(reason);
    }

    [Theory]
    [InlineData("https://127.0.0.1:54321/index.html")]
    [InlineData("javascript:alert(1)")]
    [InlineData("javascript:void(0)")]
    [InlineData("data:text/html,<h1>hacked</h1>")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("about:blank")]
    [InlineData("blob:http://127.0.0.1:54321/uuid")]
    [InlineData("ws://127.0.0.1:54321/socket")]
    public void IsAllowedNavigation_BlocksUntrustedSchemes(string targetUrl)
    {
        var policy = new WebViewNavigationPolicy(DefaultTrustedUrl);
        Assert.False(policy.IsAllowedNavigation(targetUrl));
        Assert.False(policy.ShouldAllowNavigation(targetUrl, out var reason));
        Assert.Contains("scheme", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://evil.com:54321/index.html")]
    [InlineData("http://google.com/")]
    [InlineData("http://192.168.1.1:54321/index.html")]
    [InlineData("http://localhost:54321/index.html")] // cross-origin from 127.0.0.1
    [InlineData("http://127.0.0.2:54321/index.html")]
    public void IsAllowedNavigation_BlocksForeignHosts(string targetUrl)
    {
        var policy = new WebViewNavigationPolicy(DefaultTrustedUrl);
        Assert.False(policy.IsAllowedNavigation(targetUrl));
        Assert.False(policy.ShouldAllowNavigation(targetUrl, out var reason));
        Assert.Contains("host", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://127.0.0.1:80/index.html")]
    [InlineData("http://127.0.0.1:8080/index.html")]
    [InlineData("http://127.0.0.1:54322/index.html")]
    [InlineData("http://127.0.0.1/index.html")]
    public void IsAllowedNavigation_BlocksDifferentPortsOnSameHost(string targetUrl)
    {
        var policy = new WebViewNavigationPolicy(DefaultTrustedUrl);
        Assert.False(policy.IsAllowedNavigation(targetUrl));
        Assert.False(policy.ShouldAllowNavigation(targetUrl, out var reason));
        Assert.Contains("port", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsAllowedNavigation_BlocksUserInfoInTargetUri()
    {
        var policy = new WebViewNavigationPolicy(DefaultTrustedUrl);
        var targetUrl = "http://user:password@127.0.0.1:54321/index.html";
        Assert.False(policy.IsAllowedNavigation(targetUrl));
        Assert.False(policy.ShouldAllowNavigation(targetUrl, out var reason));
        Assert.Contains("userinfo", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsAllowedNavigation_LocalhostPolicy_AllowsLocalhostAndBlocksIpv4()
    {
        var policy = new WebViewNavigationPolicy("http://localhost:3000/index.html");
        Assert.True(policy.IsAllowedNavigation("http://localhost:3000/index.html"));
        Assert.True(policy.IsAllowedNavigation("http://localhost:3000/api/status"));

        // 127.0.0.1 is a different origin from localhost
        Assert.False(policy.IsAllowedNavigation("http://127.0.0.1:3000/index.html"));
    }
}
