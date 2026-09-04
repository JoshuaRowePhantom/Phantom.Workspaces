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
    private static Uri AuthUri(string state) => new($"https://auth.test/authorize?client_id=abc&state={state}");

    [Fact]
    public async Task RedirectHandler_LaunchesBrowserAtAuthorizationUri()
    {
        var browser = new FakeSystemBrowserLauncher();
        using var handler = new McpOAuthRedirectHandler(browser, new FakeSecretProvider());
        var redirectUri = handler.EnsureListenerBound();
        var authUri = AuthUri("xyz");
        browser.OnOpen = _ => SendCallbackAsync(redirectUri, "code=abc&state=xyz");

        await handler.HandleAsync("server-a", authUri, redirectUri, CancellationToken.None);
        await browser.LastCallbackTask!;

        Assert.Equal(1, browser.OpenCount);
        Assert.Equal(authUri, browser.LastUri);
    }

    [Fact]
    public async Task RedirectHandler_ReturnsCapturedRedirectUriWithCode()
    {
        var browser = new FakeSystemBrowserLauncher();
        using var handler = new McpOAuthRedirectHandler(browser, new FakeSecretProvider());
        var redirectUri = handler.EnsureListenerBound();
        browser.OnOpen = _ => SendCallbackAsync(redirectUri, "code=the-code&state=the-state");

        var captured = await handler.HandleAsync("server-a", AuthUri("the-state"), redirectUri, CancellationToken.None);
        await browser.LastCallbackTask!;

        Assert.Equal("the-code", GetQueryValue(captured, "code"));
        Assert.Equal("the-state", GetQueryValue(captured, "state"));
    }

    [Fact]
    public async Task RedirectHandler_BindsListenerToRedirectUriLoopbackPort()
    {
        var browser = new FakeSystemBrowserLauncher();
        using var handler = new McpOAuthRedirectHandler(browser, new FakeSecretProvider());
        var redirectUri = handler.EnsureListenerBound();
        browser.OnOpen = _ => SendCallbackAsync(redirectUri, "code=abc&state=xyz");

        var captured = await handler.HandleAsync("server-a", AuthUri("xyz"), redirectUri, CancellationToken.None);
        await browser.LastCallbackTask!;

        Assert.Equal("localhost", captured.Host);
        Assert.Equal(redirectUri.Port, captured.Port);
    }

    [Fact]
    public async Task RedirectHandler_WritesCloseWindowResponse()
    {
        var browser = new FakeSystemBrowserLauncher();
        using var handler = new McpOAuthRedirectHandler(browser, new FakeSecretProvider());
        var redirectUri = handler.EnsureListenerBound();
        browser.OnOpen = _ => SendCallbackAsync(redirectUri, "code=abc&state=xyz");

        await handler.HandleAsync("server-a", AuthUri("xyz"), redirectUri, CancellationToken.None);
        var body = await browser.LastCallbackTask!;

        Assert.Contains("close this window", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RedirectHandler_WhenConsentDeclined_DoesNotLaunchBrowser()
    {
        var browser = new FakeSystemBrowserLauncher();
        using var handler = new McpOAuthRedirectHandler(browser, new FakeSecretProvider(grantConsent: false));
        var redirectUri = handler.EnsureListenerBound();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => handler.HandleAsync("server-a", AuthUri("xyz"), redirectUri, CancellationToken.None));

        Assert.Equal("User declined MCP OAuth sign-in.", exception.Message);
        Assert.Equal(0, browser.OpenCount);
    }

    [Fact]
    public async Task RedirectHandler_RequestsConsentBeforeFirstAuthorization()
    {
        var events = new List<string>();
        var consent = new FakeSecretProvider(onCalled: () => events.Add("consent"));
        var browser = new FakeSystemBrowserLauncher
        {
            OnOpenSideEffect = () => events.Add("browser"),
        };
        using var handler = new McpOAuthRedirectHandler(browser, consent);
        var redirectUri = handler.EnsureListenerBound();
        browser.OnOpen = _ => SendCallbackAsync(redirectUri, "code=abc&state=xyz");

        await handler.HandleAsync("server-a", AuthUri("xyz"), redirectUri, CancellationToken.None);
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
        using var handler = new McpOAuthRedirectHandler(browser, consent);
        var redirectUri = handler.EnsureListenerBound();

        browser.OnOpen = _ => SendCallbackAsync(redirectUri, "code=code-1&state=s1");
        await handler.HandleAsync("server-a", AuthUri("s1"), redirectUri, CancellationToken.None);
        await browser.LastCallbackTask!;

        browser.OnOpen = _ => SendCallbackAsync(redirectUri, "code=code-2&state=s2");
        await handler.HandleAsync("server-a", AuthUri("s2"), redirectUri, CancellationToken.None);
        await browser.LastCallbackTask!;

        Assert.Equal(1, consent.CallCount);
        Assert.Equal(2, browser.OpenCount);
    }

    [Fact]
    public async Task McpOAuthRedirectHandler_EnsureConsent_RequestIncludesScopeMemories()
    {
        var browser = new FakeSystemBrowserLauncher();
        var consent = new FakeSecretProvider();
        using var handler = new McpOAuthRedirectHandler(browser, consent);
        var redirectUri = handler.EnsureListenerBound();
        browser.OnOpen = _ => SendCallbackAsync(redirectUri, "code=abc&state=xyz");

        await handler.HandleAsync("server-a", AuthUri("xyz"), redirectUri, CancellationToken.None);
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
        var browser = new FakeSystemBrowserLauncher();
        var consent = new FakeSecretProvider();
        using var handler = new McpOAuthRedirectHandler(browser, consent, sessionIdentityProvider: () => "session-1");
        var redirectUri = handler.EnsureListenerBound();
        browser.OnOpen = _ => SendCallbackAsync(redirectUri, "code=abc&state=xyz");

        await handler.HandleAsync("server-a", AuthUri("xyz"), redirectUri, CancellationToken.None);
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
        using var handler1 = new McpOAuthRedirectHandler(browser1, provider1);
        var redirect1 = handler1.EnsureListenerBound();
        browser1.OnOpen = _ => SendCallbackAsync(redirect1, "code=code-1&state=s1");
        await handler1.HandleAsync("server-a", AuthUri("s1"), redirect1, CancellationToken.None);
        await browser1.LastCallbackTask!;

        Assert.Equal(1, dialog.ShowCount);

        // Second "process": brand-new handler (fresh consentedServers) and a brand-new SecretProvider,
        // but the SAME persisted store. The remembered scope must auto-approve without re-prompting.
        var browser2 = new FakeSystemBrowserLauncher();
        var provider2 = new SecretProvider(allowedStore, platformStore, dialog);
        using var handler2 = new McpOAuthRedirectHandler(browser2, provider2);
        var redirect2 = handler2.EnsureListenerBound();
        browser2.OnOpen = _ => SendCallbackAsync(redirect2, "code=code-2&state=s2");
        await handler2.HandleAsync("server-a", AuthUri("s2"), redirect2, CancellationToken.None);
        await browser2.LastCallbackTask!;

        // No second prompt: consent survived the "restart" via the persisted store.
        Assert.Equal(1, dialog.ShowCount);
        Assert.Equal(1, browser1.OpenCount);
        Assert.Equal(1, browser2.OpenCount);
    }

    [Fact]
    public async Task RedirectHandler_WhenCancelled_ThrowsAndKeepsListenerAlive()
    {
        var browser = new FakeSystemBrowserLauncher();
        using var handler = new McpOAuthRedirectHandler(browser, new FakeSecretProvider(), Timeout.InfiniteTimeSpan);
        var redirectUri = handler.EnsureListenerBound();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => handler.HandleAsync("server-a", AuthUri("xyz"), redirectUri, cts.Token));

        // The shared listener is NOT torn down by a cancelled sign-in; its own state entry is removed.
        Assert.True(handler.IsListenerBound);
        Assert.Equal(0, handler.PendingCount);
    }

    [Fact]
    public async Task RedirectHandler_WhenTimeoutElapses_ThrowsAndKeepsListenerAlive()
    {
        var browser = new FakeSystemBrowserLauncher();
        using var handler = new McpOAuthRedirectHandler(browser, new FakeSecretProvider(), TimeSpan.Zero);
        var redirectUri = handler.EnsureListenerBound();

        await Assert.ThrowsAsync<TimeoutException>(
            () => handler.HandleAsync("server-a", AuthUri("xyz"), redirectUri, CancellationToken.None));

        Assert.True(handler.IsListenerBound);
        Assert.Equal(0, handler.PendingCount);
    }

    [Fact]
    public async Task RedirectHandler_WhenRedirectContainsErrorParam_SurfacesError()
    {
        var browser = new FakeSystemBrowserLauncher();
        using var handler = new McpOAuthRedirectHandler(browser, new FakeSecretProvider());
        var redirectUri = handler.EnsureListenerBound();
        browser.OnOpen = _ => SendCallbackAsync(redirectUri, "error=access_denied&state=xyz");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync("server-a", AuthUri("xyz"), redirectUri, CancellationToken.None));
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
        Assert.Equal("localhost", options.RedirectUri!.Host);

        var redirectDelegate = options.ResolveRedirectDelegate("server-a");
        Assert.NotNull(redirectDelegate);

        // Invoking the seam-resolved delegate must route through the handler: consent is requested,
        // then the loopback wait is cancelled, proving the registered delegate is the real handler.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => redirectDelegate(AuthUri("xyz"), options.RedirectUri, cts.Token));

        Assert.Equal(1, consent.CallCount);
    }

    [Fact]
    public async Task McpOAuthRedirectHandler_RedirectCarriesErrorParam_LogsAndSurfacesDetail()
    {
        var browser = new FakeSystemBrowserLauncher();
        var logger = new CapturingLogger<McpOAuthRedirectHandler>();
        using var handler = new McpOAuthRedirectHandler(browser, new FakeSecretProvider(), logger);
        var redirectUri = handler.EnsureListenerBound();
        // The redirect carries error + error_description AND a code (which must never be logged).
        browser.OnOpen = _ => SendCallbackAsync(
            redirectUri,
            "error=access_denied&error_description=The%20user%20declined&code=must-not-be-logged&state=xyz");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync("server-a", AuthUri("xyz"), redirectUri, CancellationToken.None));
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

    // ---- Issue #1425: shared listener + state demultiplexing ----

    [Fact]
    public async Task RedirectHandler_ConcurrentSignInsForTwoServers_DoNotThrowListenerConflict()
    {
        var browser = new FakeSystemBrowserLauncher();
        using var handler = new McpOAuthRedirectHandler(browser, new FakeSecretProvider());
        var redirectUri = handler.EnsureListenerBound();
        browser.OnOpen = uri =>
        {
            var state = GetQueryValue(uri, "state");
            return SendCallbackAsync(redirectUri, $"code=code-{state}&state={state}");
        };

        // Two servers authorize at the same time. Both reuse the one shared listener; neither throws
        // an HttpListenerException prefix conflict.
        var taskA = handler.HandleAsync("server-a", AuthUri("state-a"), redirectUri, CancellationToken.None);
        var taskB = handler.HandleAsync("server-b", AuthUri("state-b"), redirectUri, CancellationToken.None);

        var captured = await Task.WhenAll(taskA, taskB);

        Assert.Equal("code-state-a", GetQueryValue(captured[0], "code"));
        Assert.Equal("code-state-b", GetQueryValue(captured[1], "code"));
        Assert.Equal(1, handler.ListenerStartCount);
    }

    [Fact]
    public async Task RedirectHandler_SecondSignInWhileFirstListenerOpen_DoesNotThrow()
    {
        var browser = new FakeSystemBrowserLauncher();
        using var handler = new McpOAuthRedirectHandler(browser, new FakeSecretProvider());
        var redirectUri = handler.EnsureListenerBound();
        using var cts = new CancellationTokenSource();

        // Server A signs in and stays pending (mode b): its loopback listener is still open.
        var taskA = handler.HandleAsync("server-a", AuthUri("state-a"), redirectUri, cts.Token);

        // Server B starts its sign-in while A is still pending — it must not throw a prefix conflict.
        browser.OnOpen = uri =>
        {
            var state = GetQueryValue(uri, "state");
            return SendCallbackAsync(redirectUri, $"code=b-code&state={state}");
        };
        var capturedB = await handler.HandleAsync("server-b", AuthUri("state-b"), redirectUri, CancellationToken.None);

        Assert.Equal("b-code", GetQueryValue(capturedB, "code"));
        Assert.Equal(1, handler.ListenerStartCount);
        Assert.False(taskA.IsCompleted);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => taskA);
    }

    [Fact]
    public async Task RedirectHandler_SharedListener_BoundOnceAcrossMultipleSignIns()
    {
        var browser = new FakeSystemBrowserLauncher();
        using var handler = new McpOAuthRedirectHandler(browser, new FakeSecretProvider());
        var redirectUri = handler.EnsureListenerBound();
        var boundPort = redirectUri.Port;
        browser.OnOpen = uri =>
        {
            var state = GetQueryValue(uri, "state");
            return SendCallbackAsync(redirectUri, $"code=c&state={state}");
        };

        for (var i = 0; i < 3; i++)
        {
            await handler.HandleAsync($"server-{i}", AuthUri($"state-{i}"), redirectUri, CancellationToken.None);
            await browser.LastCallbackTask!;
        }

        // Exactly one Start(), and the port is held continuously across every sign-in (no reserve-free).
        Assert.Equal(1, handler.ListenerStartCount);
        Assert.True(handler.IsListenerBound);
        Assert.Equal(boundPort, redirectUri.Port);

        using var probe = new HttpListener();
        probe.Prefixes.Add(McpOAuthRedirectHandler.NormalizeLoopbackPrefix(redirectUri));
        Assert.Throws<HttpListenerException>(() => probe.Start());
    }

    [Fact]
    public async Task RedirectHandler_TwoPendingSignIns_RoutesCallbackToMatchingState()
    {
        var browser = new FakeSystemBrowserLauncher();
        using var handler = new McpOAuthRedirectHandler(browser, new FakeSecretProvider());
        var redirectUri = handler.EnsureListenerBound();
        using var cts = new CancellationTokenSource();

        var taskA = handler.HandleAsync("server-a", AuthUri("state-a"), redirectUri, cts.Token);
        var taskB = handler.HandleAsync("server-b", AuthUri("state-b"), redirectUri, cts.Token);

        // Deliver only server A's callback.
        await SendCallbackAsync(redirectUri, "code=code-a&state=state-a");

        var captured = await taskA;
        Assert.Equal("code-a", GetQueryValue(captured, "code"));

        // Server B's waiter is left untouched.
        Assert.False(taskB.IsCompleted);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => taskB);
    }

    [Fact]
    public async Task RedirectHandler_CallbackWithUnknownState_DoesNotFaultPendingWaiters()
    {
        var browser = new FakeSystemBrowserLauncher();
        using var handler = new McpOAuthRedirectHandler(browser, new FakeSecretProvider());
        var redirectUri = handler.EnsureListenerBound();
        using var cts = new CancellationTokenSource();

        var taskA = handler.HandleAsync("server-a", AuthUri("state-a"), redirectUri, cts.Token);

        // A callback carrying an unrecognized state is answered with the close page and dropped.
        var body = await SendCallbackAsync(redirectUri, "code=stray&state=unknown-state");
        Assert.Contains("close this window", body, StringComparison.OrdinalIgnoreCase);
        Assert.False(taskA.IsCompleted);

        // The matching callback still completes A afterwards.
        await SendCallbackAsync(redirectUri, "code=code-a&state=state-a");
        var captured = await taskA;
        Assert.Equal("code-a", GetQueryValue(captured, "code"));

        cts.Cancel();
    }

    [Fact]
    public async Task RedirectHandler_ErrorRedirect_FaultsOnlyMatchingStateWaiter()
    {
        var browser = new FakeSystemBrowserLauncher();
        var logger = new CapturingLogger<McpOAuthRedirectHandler>();
        using var handler = new McpOAuthRedirectHandler(browser, new FakeSecretProvider(), logger);
        var redirectUri = handler.EnsureListenerBound();
        using var cts = new CancellationTokenSource();

        var taskA = handler.HandleAsync("server-a", AuthUri("state-a"), redirectUri, cts.Token);
        var taskB = handler.HandleAsync("server-b", AuthUri("state-b"), redirectUri, cts.Token);

        // An error redirect for server A (carrying a code that must NOT be logged), routed by state.
        await SendCallbackAsync(
            redirectUri,
            "error=access_denied&error_description=nope&code=must-not-be-logged&state=state-a");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => taskA);
        Assert.Equal("access_denied", exception.Data["oauth_error"]);
        Assert.Equal("nope", exception.Data["oauth_error_description"]);
        Assert.Contains("server-a", exception.Message, StringComparison.Ordinal);

        // Only server A's waiter is faulted; server B is left pending.
        Assert.False(taskB.IsCompleted);

        // #1408 privacy: only error/error_description are logged, never the code or full callback URI.
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Error && entry.Message.Contains("access_denied", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("must-not-be-logged", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("code=", StringComparison.Ordinal));

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => taskB);
    }

    [Fact]
    public async Task RedirectHandler_TimeoutOrCancel_RemovesPendingStateAndKeepsListenerAlive()
    {
        var browser = new FakeSystemBrowserLauncher();
        using var handler = new McpOAuthRedirectHandler(browser, new FakeSecretProvider(), Timeout.InfiniteTimeSpan);
        var redirectUri = handler.EnsureListenerBound();

        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => handler.HandleAsync("server-a", AuthUri("state-a"), redirectUri, cts.Token));
        }

        // The cancelled sign-in removed only its own state entry; the listener is untouched.
        Assert.Equal(0, handler.PendingCount);
        Assert.True(handler.IsListenerBound);

        // The shared listener survives — a subsequent sign-in for another server still succeeds on it.
        browser.OnOpen = uri =>
        {
            var state = GetQueryValue(uri, "state");
            return SendCallbackAsync(redirectUri, $"code=ok&state={state}");
        };
        var captured = await handler.HandleAsync("server-b", AuthUri("state-b"), redirectUri, CancellationToken.None);
        await browser.LastCallbackTask!;

        Assert.Equal("ok", GetQueryValue(captured, "code"));
        Assert.Equal(1, handler.ListenerStartCount);
    }

    [Fact]
    public void McpOAuthComposition_DoesNotReserveThenFreeAFixedPort()
    {
        var consent = new FakeSecretProvider();
        var browser = new FakeSystemBrowserLauncher();

        var options = McpOAuthComposition.CreateOptions(consent, browser);

        Assert.NotNull(options.RedirectUri);
        Assert.Equal("localhost", options.RedirectUri!.Host);

        // The redirect URI is derived from a continuously-held shared listener rather than a
        // reserve-then-free port: binding a fresh HttpListener to the same prefix conflicts because
        // the composed handler still owns the port.
        using var probe = new HttpListener();
        probe.Prefixes.Add(McpOAuthRedirectHandler.NormalizeLoopbackPrefix(options.RedirectUri!));
        Assert.Throws<HttpListenerException>(() => probe.Start());
    }

    [Fact]
    public void NormalizeLoopbackPrefix_LoopbackRedirectUri_ReturnsLocalhostPrefix()
    {
        var redirectUri = new Uri("http://127.0.0.1:52446/");

        var prefix = McpOAuthRedirectHandler.NormalizeLoopbackPrefix(redirectUri);

        Assert.Equal("http://localhost:52446/", prefix);
        Assert.Equal("localhost", new Uri(prefix).Host);
        Assert.EndsWith("/", prefix);
    }

    [Fact]
    public void CreateLoopbackRedirectUri_WhenInvoked_ReturnsLocalhostRedirectUri()
    {
        var browser = new FakeSystemBrowserLauncher();
        using var handler = new McpOAuthRedirectHandler(browser, new FakeSecretProvider());

        var redirectUri = handler.EnsureListenerBound();

        Assert.Equal("localhost", redirectUri.Host);
        Assert.Equal("http", redirectUri.Scheme);
    }

    [Fact]
    public void CreateOptions_InteractiveServer_RedirectUriUsesLocalhostHost()
    {
        var consent = new FakeSecretProvider();
        var browser = new FakeSystemBrowserLauncher();

        var options = McpOAuthComposition.CreateOptions(consent, browser);

        Assert.NotNull(options.RedirectUri);
        Assert.Equal("localhost", options.RedirectUri!.Host);
        Assert.Equal("http", options.RedirectUri.Scheme);
    }

    [Fact]
    public void LoopbackRedirect_SdkHostAndListenerHost_AreConsistent()
    {
        var browser = new FakeSystemBrowserLauncher();
        using var handler = new McpOAuthRedirectHandler(browser, new FakeSecretProvider());

        var redirectUri = handler.EnsureListenerBound();
        var listenerPrefix = McpOAuthRedirectHandler.NormalizeLoopbackPrefix(redirectUri);

        Assert.Equal(redirectUri.Host, new Uri(listenerPrefix).Host);
        Assert.Equal("localhost", redirectUri.Host);
    }

    // ---- Issue #1428: authorization URIs without a `state` parameter ----

    [Fact]
    public async Task RedirectHandler_AuthorizationUriWithoutState_InjectsStateAndCompletes()
    {
        var browser = new FakeSystemBrowserLauncher();
        using var handler = new McpOAuthRedirectHandler(browser, new FakeSecretProvider());
        var redirectUri = handler.EnsureListenerBound();
        var authUri = new Uri("https://auth.test/authorize?client_id=abc"); // no `state`
        browser.OnOpen = uri =>
        {
            var state = GetQueryValue(uri, "state");
            return SendCallbackAsync(redirectUri, $"code=the-code&state={state}");
        };

        var captured = await handler.HandleAsync("server-a", authUri, redirectUri, CancellationToken.None);
        await browser.LastCallbackTask!;

        Assert.Equal("the-code", GetQueryValue(captured, "code"));
    }

    [Fact]
    public async Task RedirectHandler_AuthorizationUriWithoutState_OpensBrowserWithSynthesizedState()
    {
        var browser = new FakeSystemBrowserLauncher();
        using var handler = new McpOAuthRedirectHandler(browser, new FakeSecretProvider());
        var redirectUri = handler.EnsureListenerBound();
        var authUri = new Uri("https://auth.test/authorize?client_id=abc"); // no `state`
        browser.OnOpen = uri =>
        {
            var state = GetQueryValue(uri, "state");
            return SendCallbackAsync(redirectUri, $"code=c&state={state}");
        };

        await handler.HandleAsync("server-a", authUri, redirectUri, CancellationToken.None);
        await browser.LastCallbackTask!;

        var openedState = GetQueryValue(browser.LastUri!, "state");
        Assert.False(string.IsNullOrEmpty(openedState));
    }

    [Fact]
    public async Task RedirectHandler_AuthorizationUriWithState_UsesProvidedStateUnchanged()
    {
        var browser = new FakeSystemBrowserLauncher();
        using var handler = new McpOAuthRedirectHandler(browser, new FakeSecretProvider());
        var redirectUri = handler.EnsureListenerBound();
        var authUri = AuthUri("provided-state");
        browser.OnOpen = _ => SendCallbackAsync(redirectUri, "code=c&state=provided-state");

        await handler.HandleAsync("server-a", authUri, redirectUri, CancellationToken.None);
        await browser.LastCallbackTask!;

        // The original URI is opened verbatim; the existing `state` is neither overwritten nor duplicated.
        Assert.Equal(authUri, browser.LastUri);
        Assert.Equal("provided-state", GetQueryValue(browser.LastUri!, "state"));
    }

    [Fact]
    public async Task RedirectHandler_TwoStatelessSignIns_AreDemultiplexedBySynthesizedState()
    {
        var browser = new FakeSystemBrowserLauncher();
        using var handler = new McpOAuthRedirectHandler(browser, new FakeSecretProvider());
        var redirectUri = handler.EnsureListenerBound();
        browser.OnOpen = uri =>
        {
            var state = GetQueryValue(uri, "state");
            return SendCallbackAsync(redirectUri, $"code=code-{state}&state={state}");
        };

        // Both authorization URIs lack `state`; each sign-in synthesizes a distinct one.
        var authA = new Uri("https://auth.test/authorize?client_id=a");
        var authB = new Uri("https://auth.test/authorize?client_id=b");
        var taskA = handler.HandleAsync("server-a", authA, redirectUri, CancellationToken.None);
        var taskB = handler.HandleAsync("server-b", authB, redirectUri, CancellationToken.None);

        var captured = await Task.WhenAll(taskA, taskB);

        var stateA = GetQueryValue(captured[0], "state");
        var stateB = GetQueryValue(captured[1], "state");
        Assert.NotEqual(stateA, stateB);
        Assert.Equal($"code-{stateA}", GetQueryValue(captured[0], "code"));
        Assert.Equal($"code-{stateB}", GetQueryValue(captured[1], "code"));
    }

    [Fact]
    public async Task RedirectHandler_StatelessSignIn_CallbackWithMismatchedState_DoesNotComplete()
    {
        var browser = new FakeSystemBrowserLauncher();
        using var handler = new McpOAuthRedirectHandler(browser, new FakeSecretProvider(), Timeout.InfiniteTimeSpan);
        var redirectUri = handler.EnsureListenerBound();
        using var cts = new CancellationTokenSource();
        var authUri = new Uri("https://auth.test/authorize?client_id=a"); // no `state`

        var task = handler.HandleAsync("server-a", authUri, redirectUri, cts.Token);
        var synthesized = GetQueryValue(browser.LastUri!, "state");
        Assert.False(string.IsNullOrEmpty(synthesized));

        // A callback carrying a different `state` is answered with the close page and dropped.
        var body = await SendCallbackAsync(redirectUri, "code=stray&state=some-other-state");
        Assert.Contains("close this window", body, StringComparison.OrdinalIgnoreCase);
        Assert.False(task.IsCompleted);

        // The callback echoing the synthesized `state` still completes the sign-in.
        await SendCallbackAsync(redirectUri, $"code=ok&state={synthesized}");
        var captured = await task;
        Assert.Equal("ok", GetQueryValue(captured, "code"));

        cts.Cancel();
    }

    [Fact]
    public async Task RedirectHandler_SynthesizedState_IsUrlSafeAndAppendedWithoutClobberingExistingQuery()
    {
        var browser = new FakeSystemBrowserLauncher();
        using var handler = new McpOAuthRedirectHandler(browser, new FakeSecretProvider());
        var redirectUri = handler.EnsureListenerBound();
        var authUri = new Uri("https://auth.test/authorize?client_id=abc&scope=openid%20profile"); // no `state`
        browser.OnOpen = uri =>
        {
            var state = GetQueryValue(uri, "state");
            return SendCallbackAsync(redirectUri, $"code=c&state={state}");
        };

        await handler.HandleAsync("server-a", authUri, redirectUri, CancellationToken.None);
        await browser.LastCallbackTask!;

        var opened = browser.LastUri!;
        // Existing query parameters are preserved.
        Assert.Equal("abc", GetQueryValue(opened, "client_id"));
        Assert.Equal("openid profile", GetQueryValue(opened, "scope"));

        // The synthesized state uses a URL-safe (base64url) alphabet.
        var state = GetQueryValue(opened, "state");
        Assert.False(string.IsNullOrEmpty(state));
        Assert.Matches("^[A-Za-z0-9_-]+$", state);
    }

    [Fact]
    public void CreateOptions_EntraPinnedAndDcrSignIn_UseSeparateLoopbackPorts()
    {
        // #1427: the composition's shared DCR listener holds a concrete loopback port, but the
        // entra-pinned credential the transport factory builds carries RedirectUri: null, so MSAL binds
        // its own listener and the two subsystems never contend for one port.
        var consent = new FakeSecretProvider();
        var browser = new FakeSystemBrowserLauncher();

        var options = McpOAuthComposition.CreateOptions(consent, browser);
        Assert.NotNull(options.RedirectUri);

        var entraOptions = EntraInteractiveCredentialFactory.BuildOptions(
            new McpEntraPinnedTokenRequest(
                "https://login.microsoftonline.com/contoso/v2.0",
                ClientId: null,
                RedirectUri: null,
                "server-a"));

        Assert.Null(entraOptions.RedirectUri);
        Assert.NotEqual(options.RedirectUri, entraOptions.RedirectUri);
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
