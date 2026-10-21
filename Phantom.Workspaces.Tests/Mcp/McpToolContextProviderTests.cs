using AgentSchema;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Authorization;
using Phantom.Workspaces.Llm.Mcp;
using Phantom.Workspaces.Services.Mcp;
using Fixture = Phantom.Workspaces.Tests.Mcp.McpOAuthCredentialAccessExecutorTests.Fixture;

namespace Phantom.Workspaces.Tests.Mcp;

public sealed class McpToolContextProviderTests
{
    [Fact]
    public async Task McpTransportFactory_CompatiblePerformedFollowers_ReconnectFromCommittedCache()
    {
        await using var fixture = new Fixture();
        fixture.Browser.Block = true;
        var first = CreateTransportAsync(fixture, "first");
        await fixture.AllowAsync();
        await fixture.Browser.Opened.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        var second = CreateTransportAsync(fixture, "second");
        await fixture.AllowAsync();
        await fixture.Acquisitions.Joined.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        fixture.Browser.Continue.TrySetResult();
        var firstTransport = await first.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await using var firstOwner = (IAsyncDisposable)firstTransport;
        var secondTransport = await second.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await using var secondOwner = (IAsyncDisposable)secondTransport;
        Assert.Null(Assert.IsType<McpBrokerClientTransport>(firstTransport).Options.OAuth);
        Assert.Null(Assert.IsType<McpBrokerClientTransport>(secondTransport).Options.OAuth);
        Assert.Equal(1, fixture.Store.Writes);
        Assert.Equal(1, fixture.Server.TokenRequests);
        Assert.Equal(1, fixture.Browser.OpenCount);

        await using var firstClient = await McpClient.CreateAsync(firstTransport, cancellationToken: TestContext.Current.CancellationToken);
        await using var secondClient = await McpClient.CreateAsync(secondTransport, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(await McpClientToolListing.ListToolsAsync(firstClient, TestContext.Current.CancellationToken));
        Assert.Empty(await McpClientToolListing.ListToolsAsync(secondClient, TestContext.Current.CancellationToken));
        await firstClient.DisposeAsync();
        await firstOwner.DisposeAsync();
        Assert.Empty(await McpClientToolListing.ListToolsAsync(secondClient, TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Browser.OpenCount);
        Assert.Equal(1, fixture.Store.Writes);
        Assert.All(fixture.Registry.GetSnapshot(), row => Assert.Equal(UserAuthorizationState.Completed, row.State));
    }

    [Fact]
    public async Task McpToolContextProvider_PendingFollower_DoesNotInheritReleaseDecision()
    {
        await using var fixture = new Fixture();
        fixture.Browser.Block = true;
        var first = CreateTransportAsync(fixture, "first");
        await fixture.AllowAsync();
        await fixture.Browser.Opened.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        var second = CreateTransportAsync(fixture, "second");
        var pending = fixture.Registry.GetSnapshot().Single(row => row.State == UserAuthorizationState.Pending);
        fixture.Browser.Continue.TrySetResult();
        var firstTransport = await first.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await using var firstOwner = (IAsyncDisposable)firstTransport;
        Assert.False(second.IsCompleted);
        var current = fixture.Registry.GetSnapshot().Single(row => row.Identifier == pending.Identifier);
        Assert.Equal(UserAuthorizationState.Pending, current.State);
        Assert.Equal(UserAuthorizationPrimaryActionKind.Allow, current.PrimaryAction);
        Assert.Equal(1, fixture.Browser.OpenCount);
        await fixture.AllowAsync(current);
        var secondTransport = await second.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await using var secondOwner = (IAsyncDisposable)secondTransport;
        await using var client = await McpClient.CreateAsync(secondTransport, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(await McpClientToolListing.ListToolsAsync(client, TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Server.TokenRequests);
        Assert.Equal(1, fixture.Browser.OpenCount);
    }

    [Fact]
    public void AccessMigration_McpProductionTree_HasNoInteractiveOAuthOptionsRedirectDelegate()
    {
        Assert.Null(typeof(McpClientToolListing).Assembly.GetType("Phantom.Workspaces.Llm.Auth.InteractiveOAuthOptions"));
        Assert.Null(typeof(McpOAuthCredentialAccessExecutor).Assembly.GetType("Phantom.Workspaces.Services.Mcp.McpOAuthRedirectHandler"));
        Assert.Null(typeof(McpOAuthComposition).GetMethod("CreateOptions",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic));
    }

    private static Task<IClientTransport> CreateTransportAsync(Fixture fixture, string session)
    {
        var tool = new McpTool
        {
            ServerName = "private-server",
            Connection = new OAuthConnection
            {
                Endpoint = fixture.Configuration.Endpoint.AbsoluteUri,
                ClientId = "private-client",
                Scopes = ["read"],
            },
        };
        return McpTransportFactory.CreateMcpTransportAsync(tool, new AgentServices
        {
            AccessBroker = fixture.Broker,
            McpOAuthCredentials = fixture.Catalog,
            AccessLineage = new CredentialUseLineage
            {
                Session = CredentialIdentityFactory.CreateLineageIdentifier("session", session),
            },
            AccessPresentationScope = AccessPresentationScope.Runtime,
        }, NullLoggerFactory.Instance, TestContext.Current.CancellationToken);
    }
}
