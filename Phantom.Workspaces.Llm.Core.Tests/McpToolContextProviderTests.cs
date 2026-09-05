using AgentSchema;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Core.Transport;
using Phantom.Workspaces.Llm.Echo;
using Phantom.Workspaces.Llm.Mcp;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Covers the #1421 DCR-first, static-fallback-second connect strategy used by
/// <c>McpToolContextProvider.ProvideAIContextAsync</c>. The provider funnels its connect through
/// <see cref="McpTransportFactory.ConnectWithDynamicRegistrationFallbackAsync{TClient}"/>, which is
/// exercised here with a fake connect delegate (a real <c>McpClient.CreateAsync</c> would require a
/// live OAuth server). A sentinel client stands in for the connected client.
/// </summary>
public sealed class McpToolContextProviderTests
{
    private const string HttpEndpoint = "https://example.test/mcp";

    private static McpTool OAuthTool(string? clientId = null)
        => new()
        {
            ServerName = "oauth-server",
            Connection = new OAuthConnection { Endpoint = HttpEndpoint, ClientId = clientId! },
        };

    private static McpException DcrRejection()
        => new("Failed to handle unauthorized response with 'Bearer' scheme. Authorization server does not support dynamic client registration");

    [Fact]
    public async Task ProvideAIContext_WhenDynamicClientRegistrationRejected_RetriesWithFallbackClientId()
    {
        var overridesSeen = new List<string?>();
        var sentinel = new object();

        var result = await McpTransportFactory.ConnectWithDynamicRegistrationFallbackAsync<object>(
            OAuthTool(clientId: null),
            (clientIdOverride, _) =>
            {
                overridesSeen.Add(clientIdOverride);
                if (clientIdOverride is null)
                {
                    throw DcrRejection();
                }

                return Task.FromResult(sentinel);
            },
            NullLogger.Instance,
            "oauth-server",
            CancellationToken.None);

        Assert.Same(sentinel, result);

        // Exactly two attempts: the DCR-first attempt (null override) then the single static fallback.
        Assert.Equal(2, overridesSeen.Count);
        Assert.Null(overridesSeen[0]);
        Assert.Equal(McpTransportFactory.DefaultDynamicRegistrationFallbackClientId, overridesSeen[1]);
    }

    [Fact]
    public async Task ProvideAIContext_WhenDcrRejectedAndRetryFails_SurfacesOriginalDiagnostic()
    {
        var attempts = 0;
        var retryFailure = new McpException(
            "Failed to handle unauthorized response with 'Bearer' scheme. Dynamic client registration failed with status BadRequest");

        var thrown = await Assert.ThrowsAsync<McpException>(() =>
            McpTransportFactory.ConnectWithDynamicRegistrationFallbackAsync<object>(
                OAuthTool(clientId: null),
                (clientIdOverride, _) =>
                {
                    attempts++;
                    throw clientIdOverride is null ? DcrRejection() : retryFailure;
                },
                NullLogger.Instance,
                "oauth-server",
                CancellationToken.None));

        // The single retry ran (no infinite retry) and its failure propagated so the caller's
        // #1408 diagnostic is shown.
        Assert.Equal(2, attempts);
        Assert.Same(retryFailure, thrown);
    }

    [Fact]
    public async Task ProvideAIContext_WhenNonDcrFailure_DoesNotRetry()
    {
        var attempts = 0;
        var networkFailure = new McpException("The remote name could not be resolved: 'auth.example.test'");

        var thrown = await Assert.ThrowsAsync<McpException>(() =>
            McpTransportFactory.ConnectWithDynamicRegistrationFallbackAsync<object>(
                OAuthTool(clientId: null),
                (_, _) =>
                {
                    attempts++;
                    throw networkFailure;
                },
                NullLogger.Instance,
                "oauth-server",
                CancellationToken.None));

        Assert.Equal(1, attempts);
        Assert.Same(networkFailure, thrown);
    }

    [Fact]
    public async Task ProvideAIContext_WhenExplicitClientIdConfigured_DoesNotRetryOnDcrShapedFailure()
    {
        var attempts = 0;

        var thrown = await Assert.ThrowsAsync<McpException>(() =>
            McpTransportFactory.ConnectWithDynamicRegistrationFallbackAsync<object>(
                OAuthTool(clientId: "explicit-client-id"),
                (_, _) =>
                {
                    attempts++;
                    throw DcrRejection();
                },
                NullLogger.Instance,
                "oauth-server",
                CancellationToken.None));

        Assert.Equal(1, attempts);
        Assert.IsType<McpException>(thrown);
    }

    [Fact]
    public async Task ProvideAIContext_WhenDcrSucceeds_DoesNotUseFallback()
    {
        var overridesSeen = new List<string?>();
        var sentinel = new object();

        var result = await McpTransportFactory.ConnectWithDynamicRegistrationFallbackAsync<object>(
            OAuthTool(clientId: null),
            (clientIdOverride, _) =>
            {
                overridesSeen.Add(clientIdOverride);
                return Task.FromResult(sentinel);
            },
            NullLogger.Instance,
            "oauth-server",
            CancellationToken.None);

        Assert.Same(sentinel, result);
        Assert.Single(overridesSeen);
        Assert.Null(overridesSeen[0]);
    }

    // --- issue #1447: terminal-failure latch on the provider instance -----------------------------

    [Fact]
    public async Task McpToolContextProvider_InitFailure_MarksTerminalAndNotRetriedOnNextInvocation()
    {
        var attempts = 0;
        var provider = CreateProvider(_ =>
        {
            attempts++;
            throw new McpException("connect failed");
        });
        await using var disposable = provider;

        // First invocation attempts the connect, which fails and sets the terminal latch.
        await Assert.ThrowsAsync<McpException>(() => InvokeAsync(provider));

        // The second invocation short-circuits on the latch and must NOT retry the connect.
        var tools = await InvokeAsync(provider);

        Assert.Empty(tools);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task McpToolContextProvider_SuccessfulInit_CachesSingleClientAcrossInvocations()
    {
        var attempts = 0;
        var sampleTool = AIFunctionFactory.Create(() => "ok", "sampleTool");
        var provider = CreateProvider(_ =>
        {
            attempts++;
            return Task.FromResult<AITool[]>([sampleTool]);
        });
        await using var disposable = provider;

        var first = await InvokeAsync(provider);
        var second = await InvokeAsync(provider);

        // A successful init is cached: the connect ran exactly once, and tools are still returned.
        Assert.Equal(1, attempts);
        Assert.Single(first);
        Assert.Single(second);
        Assert.Equal("sampleTool", second[0].Name);
    }

    [Fact]
    public async Task McpToolContextProvider_TerminalFailure_DoesNotRelaunchOAuthRedirect()
    {
        var redirectLaunches = 0;
        var provider = CreateProvider(_ =>
        {
            // Model the OAuth authorization-code flow launching the browser redirect, then failing.
            redirectLaunches++;
            throw new McpException("authorization failed");
        });
        await using var disposable = provider;

        await Assert.ThrowsAsync<McpException>(() => InvokeAsync(provider));
        _ = await InvokeAsync(provider);
        _ = await InvokeAsync(provider);

        // The redirect (browser relaunch) fires at most once despite repeated invocations.
        Assert.Equal(1, redirectLaunches);
    }

    [Fact]
    public async Task McpToolContextProvider_ResetInitialization_ClearsFailureAndAllowsOneNewAttempt()
    {
        var attempts = 0;
        var provider = CreateProvider(_ =>
        {
            attempts++;
            throw new McpException("connect failed");
        });
        await using var disposable = provider;

        await Assert.ThrowsAsync<McpException>(() => InvokeAsync(provider));
        Assert.Equal(1, attempts);

        // Latched: further invocations do not retry.
        _ = await InvokeAsync(provider);
        Assert.Equal(1, attempts);

        provider.ResetInitialization();

        // Exactly one fresh attempt is made after reset (it fails again and re-latches).
        await Assert.ThrowsAsync<McpException>(() => InvokeAsync(provider));
        Assert.Equal(2, attempts);

        _ = await InvokeAsync(provider);
        Assert.Equal(2, attempts);
    }

    private static McpToolContextProvider CreateProvider(Func<CancellationToken, Task<AITool[]>> initialize)
        => new(
            OAuthTool(),
            NullLoggerFactory.Instance,
            ExecutorTarget.AgentExecutor,
            services: null,
            initializeOverride: initialize);

    private static async Task<AITool[]> InvokeAsync(McpToolContextProvider provider)
    {
        var agent = new ChatClientAgent(new EchoChatClient(), new ChatClientAgentOptions
        {
            UseProvidedChatClientAsIs = true,
        });
        var session = await agent.CreateSessionAsync(CancellationToken.None);
        return await AIContextProviderToolReader.GetToolsAsync(provider, agent, session, CancellationToken.None);
    }
}
