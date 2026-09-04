using AgentSchema;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
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
}
