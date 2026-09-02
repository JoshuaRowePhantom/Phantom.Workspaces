using System.Net;
using System.Net.Http;
using Phantom.Workspaces.Llm.Mcp;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Services.Mcp;

namespace Phantom.Workspaces.Tests.Mcp;

public sealed class McpOAuthRedirectHandlerTests
{
    private static readonly Uri AuthorizationUri = new("https://auth.test/authorize?client_id=abc");

    [Fact]
    public async Task RedirectHandler_LaunchesBrowserAtAuthorizationUri()
    {
        var redirectUri = McpOAuthComposition.CreateLoopbackRedirectUri();
        var browser = new FakeSystemBrowserLauncher();
        browser.OnOpen = _ => SendCallbackAsync(redirectUri, "code=abc&state=xyz");
        var handler = new McpOAuthRedirectHandler(browser, new FakeSecretProvider());

        await handler.HandleAsync("server-a", AuthorizationUri, redirectUri, CancellationToken.None);
        await browser.LastCallbackTask!;

        Assert.Equal(1, browser.OpenCount);
        Assert.Equal(AuthorizationUri, browser.LastUri);
    }

    [Fact]
    public async Task RedirectHandler_ReturnsCapturedRedirectUriWithCode()
    {
        var redirectUri = McpOAuthComposition.CreateLoopbackRedirectUri();
        var browser = new FakeSystemBrowserLauncher();
        browser.OnOpen = _ => SendCallbackAsync(redirectUri, "code=the-code&state=the-state");
        var handler = new McpOAuthRedirectHandler(browser, new FakeSecretProvider());

        var captured = await handler.HandleAsync("server-a", AuthorizationUri, redirectUri, CancellationToken.None);
        await browser.LastCallbackTask!;

        Assert.Equal("the-code", GetQueryValue(captured, "code"));
        Assert.Equal("the-state", GetQueryValue(captured, "state"));
    }

    [Fact]
    public async Task RedirectHandler_BindsListenerToRedirectUriLoopbackPort()
    {
        var redirectUri = McpOAuthComposition.CreateLoopbackRedirectUri();
        var browser = new FakeSystemBrowserLauncher();
        browser.OnOpen = _ => SendCallbackAsync(redirectUri, "code=abc&state=xyz");
        var handler = new McpOAuthRedirectHandler(browser, new FakeSecretProvider());

        var captured = await handler.HandleAsync("server-a", AuthorizationUri, redirectUri, CancellationToken.None);
        await browser.LastCallbackTask!;

        Assert.Equal("127.0.0.1", captured.Host);
        Assert.Equal(redirectUri.Port, captured.Port);
    }

    [Fact]
    public async Task RedirectHandler_WritesCloseWindowResponse()
    {
        var redirectUri = McpOAuthComposition.CreateLoopbackRedirectUri();
        var browser = new FakeSystemBrowserLauncher();
        browser.OnOpen = _ => SendCallbackAsync(redirectUri, "code=abc&state=xyz");
        var handler = new McpOAuthRedirectHandler(browser, new FakeSecretProvider());

        await handler.HandleAsync("server-a", AuthorizationUri, redirectUri, CancellationToken.None);
        var body = await browser.LastCallbackTask!;

        Assert.Contains("close this window", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RedirectHandler_WhenConsentDeclined_DoesNotLaunchBrowser()
    {
        var redirectUri = McpOAuthComposition.CreateLoopbackRedirectUri();
        var browser = new FakeSystemBrowserLauncher();
        var handler = new McpOAuthRedirectHandler(browser, new FakeSecretProvider(grantConsent: false));

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => handler.HandleAsync("server-a", AuthorizationUri, redirectUri, CancellationToken.None));

        Assert.Equal("User declined MCP OAuth sign-in.", exception.Message);
        Assert.Equal(0, browser.OpenCount);
    }

    [Fact]
    public async Task RedirectHandler_RequestsConsentBeforeFirstAuthorization()
    {
        var events = new List<string>();
        var redirectUri = McpOAuthComposition.CreateLoopbackRedirectUri();
        var consent = new FakeSecretProvider(onCalled: () => events.Add("consent"));
        var browser = new FakeSystemBrowserLauncher
        {
            OnOpenSideEffect = () => events.Add("browser"),
        };
        browser.OnOpen = _ => SendCallbackAsync(redirectUri, "code=abc&state=xyz");
        var handler = new McpOAuthRedirectHandler(browser, consent);

        await handler.HandleAsync("server-a", AuthorizationUri, redirectUri, CancellationToken.None);
        await browser.LastCallbackTask!;

        Assert.Equal(1, consent.CallCount);
        Assert.Equal(["consent", "browser"], events);
        Assert.Contains(consent.RequestedSecretNames, name => name.Contains("server-a", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RedirectHandler_RemembersConsentForSession_DoesNotRepromptOnRefresh()
    {
        var consent = new FakeSecretProvider();
        var browser = new FakeSystemBrowserLauncher();
        var handler = new McpOAuthRedirectHandler(browser, consent);

        var firstRedirect = McpOAuthComposition.CreateLoopbackRedirectUri();
        browser.OnOpen = _ => SendCallbackAsync(firstRedirect, "code=code-1&state=xyz");
        await handler.HandleAsync("server-a", AuthorizationUri, firstRedirect, CancellationToken.None);
        await browser.LastCallbackTask!;

        var secondRedirect = McpOAuthComposition.CreateLoopbackRedirectUri();
        browser.OnOpen = _ => SendCallbackAsync(secondRedirect, "code=code-2&state=xyz");
        await handler.HandleAsync("server-a", AuthorizationUri, secondRedirect, CancellationToken.None);
        await browser.LastCallbackTask!;

        Assert.Equal(1, consent.CallCount);
        Assert.Equal(2, browser.OpenCount);
    }

    [Fact]
    public async Task RedirectHandler_WhenCancelled_ThrowsAndStopsListener()
    {
        var redirectUri = McpOAuthComposition.CreateLoopbackRedirectUri();
        var browser = new FakeSystemBrowserLauncher();
        var handler = new McpOAuthRedirectHandler(browser, new FakeSecretProvider(), Timeout.InfiniteTimeSpan);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => handler.HandleAsync("server-a", AuthorizationUri, redirectUri, cts.Token));

        AssertPortReleased(redirectUri);
    }

    [Fact]
    public async Task RedirectHandler_WhenTimeoutElapses_ThrowsAndStopsListener()
    {
        var redirectUri = McpOAuthComposition.CreateLoopbackRedirectUri();
        var browser = new FakeSystemBrowserLauncher();
        var handler = new McpOAuthRedirectHandler(browser, new FakeSecretProvider(), TimeSpan.Zero);

        await Assert.ThrowsAsync<TimeoutException>(
            () => handler.HandleAsync("server-a", AuthorizationUri, redirectUri, CancellationToken.None));

        AssertPortReleased(redirectUri);
    }

    [Fact]
    public async Task RedirectHandler_WhenRedirectContainsErrorParam_SurfacesError()
    {
        var redirectUri = McpOAuthComposition.CreateLoopbackRedirectUri();
        var browser = new FakeSystemBrowserLauncher();
        browser.OnOpen = _ => SendCallbackAsync(redirectUri, "error=access_denied");
        var handler = new McpOAuthRedirectHandler(browser, new FakeSecretProvider());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync("server-a", AuthorizationUri, redirectUri, CancellationToken.None));
        await browser.LastCallbackTask!;

        Assert.Contains("access_denied", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedirectHandler_IsRegisteredIntoTransportOAuthSeam()
    {
        var consent = new FakeSecretProvider();
        var browser = new FakeSystemBrowserLauncher();

        var options = McpOAuthComposition.CreateOptions(consent, browser);

        Assert.NotNull(options.RedirectDelegateProvider);
        Assert.NotNull(options.RedirectUri);
        Assert.Equal("127.0.0.1", options.RedirectUri!.Host);

        var redirectDelegate = options.ResolveRedirectDelegate("server-a");
        Assert.NotNull(redirectDelegate);

        // Invoking the seam-resolved delegate must route through the handler: consent is requested,
        // then the loopback wait is cancelled, proving the registered delegate is the real handler.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => redirectDelegate(AuthorizationUri, options.RedirectUri, cts.Token));

        Assert.Equal(1, consent.CallCount);
    }

    private static async Task<string> SendCallbackAsync(Uri redirectUri, string rawQuery)
    {
        using var client = new HttpClient();
        var target = new UriBuilder(redirectUri)
        {
            Path = "/callback",
            Query = rawQuery,
        }.Uri;
        using var response = await client.GetAsync(target);
        return await response.Content.ReadAsStringAsync();
    }

    private static void AssertPortReleased(Uri redirectUri)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add(McpOAuthRedirectHandler.NormalizeLoopbackPrefix(redirectUri));
        listener.Start();
        listener.Stop();
    }

    private static string? GetQueryValue(Uri uri, string key)
    {
        foreach (var segment in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = segment.IndexOf('=');
            var name = separatorIndex >= 0 ? segment[..separatorIndex] : segment;
            if (string.Equals(Uri.UnescapeDataString(name), key, StringComparison.Ordinal))
            {
                var value = separatorIndex >= 0 ? segment[(separatorIndex + 1)..] : string.Empty;
                return Uri.UnescapeDataString(value);
            }
        }

        return null;
    }

    private sealed class FakeSystemBrowserLauncher : ISystemBrowserLauncher
    {
        public int OpenCount { get; private set; }

        public Uri? LastUri { get; private set; }

        public Func<Uri, Task<string>>? OnOpen { get; set; }

        public Action? OnOpenSideEffect { get; set; }

        public Task<string>? LastCallbackTask { get; private set; }

        public void Open(Uri uri)
        {
            this.OpenCount++;
            this.LastUri = uri;
            this.OnOpenSideEffect?.Invoke();
            if (this.OnOpen is { } callback)
            {
                this.LastCallbackTask = callback(uri);
            }
        }
    }

    private sealed class FakeSecretProvider : ISecretProvider
    {
        private readonly bool grantConsent;
        private readonly Action? onCalled;

        public FakeSecretProvider(bool grantConsent = true, Action? onCalled = null)
        {
            this.grantConsent = grantConsent;
            this.onCalled = onCalled;
        }

        public int CallCount { get; private set; }

        public List<string> RequestedSecretNames { get; } = [];

        public Task<RequestSecretsResult?> RequestSecretsAsync(
            IReadOnlyList<SecretRequest> requests,
            CancellationToken cancellationToken)
        {
            this.CallCount++;
            foreach (var request in requests)
            {
                this.RequestedSecretNames.Add(request.SecretName);
            }

            this.onCalled?.Invoke();
            return Task.FromResult<RequestSecretsResult?>(
                this.grantConsent ? new RequestSecretsResult([], []) : null);
        }
    }
}
