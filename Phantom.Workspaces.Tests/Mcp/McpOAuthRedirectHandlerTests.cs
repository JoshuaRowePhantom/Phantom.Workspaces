using System.Net;
using System.Net.Http;
using System.Security;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Llm.Mcp;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Services.Mcp;
using Phantom.Workspaces.Services.Secrets;

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
    public async Task McpOAuthRedirectHandler_EnsureConsent_RequestIncludesScopeMemories()
    {
        var redirectUri = McpOAuthComposition.CreateLoopbackRedirectUri();
        var browser = new FakeSystemBrowserLauncher();
        browser.OnOpen = _ => SendCallbackAsync(redirectUri, "code=abc&state=xyz");
        var consent = new FakeSecretProvider();
        var handler = new McpOAuthRedirectHandler(browser, consent);

        await handler.HandleAsync("server-a", AuthorizationUri, redirectUri, CancellationToken.None);
        await browser.LastCallbackTask!;

        var request = Assert.Single(consent.RequestedRequests);
        Assert.Equal("McpOAuth:server-a", request.SecretName);
        Assert.NotEmpty(request.Memories);
        Assert.Contains(request.Memories, memory => memory.DisplayString == "All Uses");
        Assert.Contains(request.Memories, memory => memory.DisplayString == "Always Ask");

        // The source ComboBox is populated with a single interactive-OAuth source instead of blank.
        Assert.IsType<OAuthSecretSource>(request.DefaultSecretSource);
        Assert.Contains(request.CandidateSecretSources, source => source is OAuthSecretSource);

        // The consent key/memories must never embed the authorization code or any token material.
        Assert.DoesNotContain(request.Memories, memory => memory.Hash.Contains("abc", StringComparison.Ordinal));
    }

    [Fact]
    public async Task McpOAuthRedirectHandler_EnsureConsent_WithSessionIdentity_IncludesSessionScope()
    {
        var redirectUri = McpOAuthComposition.CreateLoopbackRedirectUri();
        var browser = new FakeSystemBrowserLauncher();
        browser.OnOpen = _ => SendCallbackAsync(redirectUri, "code=abc&state=xyz");
        var consent = new FakeSecretProvider();
        var handler = new McpOAuthRedirectHandler(browser, consent, sessionIdentityProvider: () => "session-1");

        await handler.HandleAsync("server-a", AuthorizationUri, redirectUri, CancellationToken.None);
        await browser.LastCallbackTask!;

        var request = Assert.Single(consent.RequestedRequests);
        Assert.Contains(request.Memories, memory => memory.Scope == SecretUseScope.SessionIdentity);
        Assert.Contains(request.Memories, memory => memory.DisplayString == "This Session");
    }

    [Fact]
    public async Task McpOAuthRedirectHandler_ConsentRemembered_DoesNotRepromptAcrossRestart()
    {
        // The shared allowed-secrets store stands in for the persisted allowed-secrets.json that
        // survives a restart. Both "processes" read/write the same store via the real SecretProvider.
        var allowedStore = new InMemoryAllowedSecretsStore();
        var platformStore = new NoopPlatformSecretStore();
        var dialog = new BroadScopeDialogHost();

        // First "process": fresh handler + fresh SecretProvider. Consent is prompted once and the
        // chosen broad (non-AlwaysAsk) scope is persisted to the shared store.
        var browser1 = new FakeSystemBrowserLauncher();
        var provider1 = new SecretProvider(allowedStore, platformStore, dialog);
        var handler1 = new McpOAuthRedirectHandler(browser1, provider1);
        var redirect1 = McpOAuthComposition.CreateLoopbackRedirectUri();
        browser1.OnOpen = _ => SendCallbackAsync(redirect1, "code=code-1&state=xyz");
        await handler1.HandleAsync("server-a", AuthorizationUri, redirect1, CancellationToken.None);
        await browser1.LastCallbackTask!;

        Assert.Equal(1, dialog.ShowCount);

        // Second "process": brand-new handler (fresh consentedServers) and a brand-new SecretProvider,
        // but the SAME persisted store. The remembered scope must auto-approve without re-prompting.
        var browser2 = new FakeSystemBrowserLauncher();
        var provider2 = new SecretProvider(allowedStore, platformStore, dialog);
        var handler2 = new McpOAuthRedirectHandler(browser2, provider2);
        var redirect2 = McpOAuthComposition.CreateLoopbackRedirectUri();
        browser2.OnOpen = _ => SendCallbackAsync(redirect2, "code=code-2&state=xyz");
        await handler2.HandleAsync("server-a", AuthorizationUri, redirect2, CancellationToken.None);
        await browser2.LastCallbackTask!;

        // No second prompt: consent survived the "restart" via the persisted store.
        Assert.Equal(1, dialog.ShowCount);
        Assert.Equal(1, browser1.OpenCount);
        Assert.Equal(1, browser2.OpenCount);
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

    [Fact]
    public async Task McpOAuthRedirectHandler_RedirectCarriesErrorParam_LogsAndSurfacesDetail()
    {
        var redirectUri = McpOAuthComposition.CreateLoopbackRedirectUri();
        var browser = new FakeSystemBrowserLauncher();
        // The redirect carries error + error_description AND a code (which must never be logged).
        browser.OnOpen = _ => SendCallbackAsync(
            redirectUri,
            "error=access_denied&error_description=The%20user%20declined&code=must-not-be-logged&state=xyz");
        var logger = new CapturingLogger<McpOAuthRedirectHandler>();
        var handler = new McpOAuthRedirectHandler(browser, new FakeSecretProvider(), logger);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync("server-a", AuthorizationUri, redirectUri, CancellationToken.None));
        await browser.LastCallbackTask!;

        // The decoded error/error_description are carried on the exception for the surfaced detail.
        Assert.Equal("access_denied", exception.Data["oauth_error"]);
        Assert.Equal("The user declined", exception.Data["oauth_error_description"]);

        // The handler logged the error code.
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Error && entry.Message.Contains("access_denied", StringComparison.Ordinal));

        // The full redirect URI (with `code`) must NOT appear in any log entry.
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("must-not-be-logged", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("code=", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains(redirectUri.ToString(), StringComparison.Ordinal));
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

        public List<SecretRequest> RequestedRequests { get; } = [];

        public SecretRequest? LastRequest { get; private set; }

        public Task<RequestSecretsResult?> RequestSecretsAsync(
            IReadOnlyList<SecretRequest> requests,
            CancellationToken cancellationToken)
        {
            this.CallCount++;
            foreach (var request in requests)
            {
                this.RequestedSecretNames.Add(request.SecretName);
                this.RequestedRequests.Add(request);
                this.LastRequest = request;
            }

            this.onCalled?.Invoke();
            return Task.FromResult<RequestSecretsResult?>(
                this.grantConsent ? new RequestSecretsResult([], []) : null);
        }
    }

    private sealed class InMemoryAllowedSecretsStore : IAllowedSecretsStore
    {
        private readonly Dictionary<string, MemorizedSecret> records = new(StringComparer.Ordinal);

        public Task<MemorizedSecret?> TryGetAsync(string hash, CancellationToken ct)
            => Task.FromResult(this.records.TryGetValue(hash, out var record) ? record : null);

        public Task PutAsync(string hash, MemorizedSecret record, CancellationToken ct)
        {
            this.records[hash] = record;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string hash, CancellationToken ct)
        {
            this.records.Remove(hash);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, MemorizedSecret>> LoadAllAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, MemorizedSecret>>(
                new Dictionary<string, MemorizedSecret>(this.records, StringComparer.Ordinal));
    }

    private sealed class NoopPlatformSecretStore : IPlatformSecretStore
    {
        public Task<SecureString?> ReadAsync(string name, CancellationToken ct)
            => Task.FromResult<SecureString?>(null);

        public Task WriteAsync(string name, SecureString value, CancellationToken ct)
            => Task.CompletedTask;

        public Task DeleteAsync(string name, CancellationToken ct)
            => Task.CompletedTask;

        public Task<IReadOnlyList<string>> EnumerateNamesAsync(string prefix, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class BroadScopeDialogHost : ISecretUseDialogHost
    {
        public int ShowCount { get; private set; }

        public Task<SecretUseDialogResult> ShowAsync(SecretUseDialogInput input, CancellationToken ct)
        {
            this.ShowCount++;
            var rows = input.Rows
                .Select(request =>
                {
                    // Choose the broadest non-AlwaysAsk scope (a persistable memory) so the grant is
                    // remembered across the "restart".
                    var chosen = request.Memories.First(memory => memory.Scope != SecretUseScope.AlwaysAsk);
                    var source = request.DefaultSecretSource ?? request.CandidateSecretSources[0];
                    return new SecretUseDialogRow(request, chosen, source);
                })
                .ToArray();
            return Task.FromResult(new SecretUseDialogResult(true, rows));
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            this.Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
