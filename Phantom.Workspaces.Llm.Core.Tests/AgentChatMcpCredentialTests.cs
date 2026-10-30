using System.Collections.Specialized;
using AgentSchema;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class AgentChatMcpCredentialTests
{
    [Fact]
    public async Task MissingBroker_OptionalSecretFailureDoesNotDisableAnonymousSibling()
    {
        await using var server = await TestMcpServerProcess.StartAsync();
        var environment = new McpCredentialTestEnvironment();
        var client = McpCredentialTestEnvironment.Client();
        var definition = PhantomAgentSchema.AgentDefinitionFromJson(
            McpCredentialTestEnvironment.Definition(sibling: true, anonymousSibling: true)
                .ToJson().Replace("https://healthy.test/mcp", server.BoundUrl.ToString()))!;
        await using var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = definition,
            AgentServices = environment.Services(client) with { AccessBroker = null },
            PersistenceStoreFactory = (_, _) => ValueTask.FromResult<IAgentPersistenceStore>(new InMemoryAgentPersistenceStore()),
        });
        await chat.Initialization;
        Assert.False(chat.GetToolSnapshot().Single(node => node.Name == "optional").IsEnabled);
        Assert.Single(chat.GetToolSnapshot().Single(node => node.Name == "healthy").Children);
        await McpCredentialTestEnvironment.AssertTurnCompletesAsync(chat);
        Assert.Equal("ping", Assert.Single(client.LastRequestOptions!.Tools!).Name);
        Assert.Empty(environment.Requests);
    }

    [Fact]
    public async Task RetryAfterConnectionFailure_ReleasesFailedGenerationAndKeepsCoreAndSiblingLeases()
    {
        var environment = new McpCredentialTestEnvironment { RejectOptionalRequests = true };
        var chat = await CreateAsync(environment, McpCredentialTestEnvironment.Client(), required: true, sibling: true);
        try
        {
            await chat.Initialization;
            var failed = Assert.Single(chat.GetToolSnapshot(), node => node.Name == "optional");
            Assert.False(failed.IsEnabled);
            Assert.Single(chat.GetToolSnapshot().Single(node => node.Name == "healthy").Children);
            Assert.Equal(3, environment.Results.Count);
            var failedLeases = 0;
            foreach (var result in environment.Results)
                if (await result.Lease.UseAsync((_, _) => ValueTask.FromResult(true), CancellationToken.None)
                    is SensitiveValueUseFailed<bool>)
                    failedLeases++;
            Assert.Equal(1, failedLeases);
            environment.RejectOptionalRequests = false;
            var ready = WaitForServerAsync(chat, "optional", node => node.Children.Count == 1);
            await Task.WhenAll(chat.SetToolEnabledAsync(failed.Id, true), chat.SetToolEnabledAsync(failed.Id, true));
            await ready;
            Assert.Equal(2, environment.Requests.Count(request => request.Reference.Name == "Optional"));
            Assert.Single(environment.Requests, request => request.Reference.Name == "Healthy");
            Assert.Single(environment.Requests, request => request.Reference.Name == "Required");
            Assert.Equal(4, environment.Results.Count);
        }
        finally { await chat.DisposeAsync(); }
        Assert.Equal(2, environment.TransportDisposals["optional.test"]);
        Assert.Equal(1, environment.TransportDisposals["healthy.test"]);
        foreach (var result in environment.Results)
            Assert.IsType<SensitiveValueUseFailed<bool>>(await result.Lease.UseAsync(
                (_, _) => ValueTask.FromResult(true), CancellationToken.None));
    }

    [Fact]
    public async Task DisableDuringAuthorization_LateDiscoveryRemainsDisabledAndReenableUsesCachedGeneration()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var environment = new McpCredentialTestEnvironment();
        environment.Acquire = async (_, cancellation) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellation);
            return environment.Success();
        };
        await using var chat = await CreateAsync(environment, McpCredentialTestEnvironment.Client());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var pending = Assert.Single(chat.GetToolSnapshot());
        await chat.SetToolEnabledAsync(pending.Id, false);
        release.TrySetResult();
        await chat.Initialization;
        var loaded = Assert.Single(chat.GetToolSnapshot());
        Assert.False(loaded.IsEnabled);
        Assert.False(Assert.Single(loaded.Children).IsEnabled);
        await chat.SetToolEnabledAsync(loaded.Id, true);
        Assert.True(Assert.Single(chat.GetToolSnapshot()).IsEnabled);
        Assert.Single(environment.Requests);
        Assert.True(Assert.Single(chat.GetToolSnapshot().Single().Children).IsEnabled);
    }

    [Fact]
    public async Task DisposeAsync_AuthorizationReturnsAfterCancellation_ReleasesLateLeaseWithoutPublishingTools()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var environment = new McpCredentialTestEnvironment();
        environment.Acquire = async (request, cancellation) =>
        {
            if (request.Reference.Name == "Optional")
            {
                using var registration = cancellation.Register(() => cancelled.TrySetResult());
                entered.TrySetResult();
                await release.Task;
            }
            return environment.Success();
        };
        var chat = await CreateAsync(environment, McpCredentialTestEnvironment.Client(), required: true, sibling: true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await WaitForServerAsync(chat, "healthy", node => node.Children.Count == 1);
        var disposal = chat.DisposeAsync().AsTask();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.False(disposal.IsCompleted);
        release.TrySetResult();
        await disposal;
        Assert.Empty(chat.GetToolSnapshot().Single(node => node.Name == "optional").Children);
        Assert.DoesNotContain("optional.test", environment.HttpRequests.Keys);
        foreach (var result in environment.Results)
            Assert.IsType<SensitiveValueUseFailed<bool>>(await result.Lease.UseAsync(
                (_, _) => ValueTask.FromResult(true), CancellationToken.None));
    }

    [Fact]
    public async Task RunProcessLoopAsync_OptionalMcpAuthorizationIsPending_ProcessesTurnWithAvailableTools()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var environment = new McpCredentialTestEnvironment();
        environment.Acquire = async (request, cancellation) =>
        {
            if (request.Reference.Name == "Optional")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellation);
            }
            return environment.Success();
        };
        var client = McpCredentialTestEnvironment.Client();
        await using var chat = await CreateAsync(environment, client, sibling: true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await WaitForServerAsync(chat, "healthy", node => node.Children.Count == 1);
        var pending = Assert.Single(chat.GetToolSnapshot(), node => node.Name == "optional");
        Assert.Contains("authorization", pending.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(pending.Children);
        Assert.Contains(chat.RunningItems.SelectMany(item => item.Items)
            .SelectMany(item => item.Contents).OfType<TextContent>(),
            content => content.Text.Contains("optional", StringComparison.Ordinal)
                && content.Text.Contains("authorization", StringComparison.OrdinalIgnoreCase));
        Assert.False(chat.Initialization.IsCompleted);
        await McpCredentialTestEnvironment.AssertTurnCompletesAsync(chat);
        Assert.Equal("healthy_tool", Assert.Single(client.LastRequestOptions!.Tools!).Name);
        Assert.Contains("optional", client.LastRequestOptions.Instructions, StringComparison.Ordinal);
        Assert.Contains("authorization", client.LastRequestOptions.Instructions, StringComparison.OrdinalIgnoreCase);
        release.TrySetResult();
        await chat.Initialization;
        Assert.Equal(2, chat.GetToolSnapshot().Sum(node => node.Children.Count));
    }

    [Fact]
    public async Task RetryAfterProvisioning_PublishesFreshSelectedServerWithoutTouchingHealthySibling()
    {
        var environment = new McpCredentialTestEnvironment();
        var provisioned = false;
        environment.Acquire = (request, _) => Task.FromResult(
            request.Reference.Name == "Optional" && !provisioned
                ? McpCredentialTestEnvironment.Failure("denied") : environment.Success());
        var client = McpCredentialTestEnvironment.Client();
        var chat = await CreateAsync(environment, client, required: true, sibling: true);
        try
        {
            await chat.Initialization;
            var before = chat.GetToolSnapshot();
            var failed = Assert.Single(before, node => node.Name == "optional");
            var healthy = Assert.Single(before, node => node.Name == "healthy");
            Assert.False(failed.IsEnabled);
            Assert.Single(healthy.Children);
            Assert.Equal(2, environment.Results.Count); // Required + healthy.
            provisioned = true;
            var ready = WaitForServerAsync(chat, "optional", node => node.Children.Count == 1);
            await Task.WhenAll(
                chat.SetToolEnabledAsync(failed.Id, true),
                chat.SetToolEnabledAsync(failed.Id, true));
            await ready;
            Assert.Equal(2, environment.Requests.Count(request => request.Reference.Name == "Optional"));
            Assert.Single(environment.Requests, request => request.Reference.Name == "Healthy");
            Assert.Single(environment.Requests, request => request.Reference.Name == "Required");
            Assert.Equal("Loaded 1 tool.", Assert.Single(chat.GetToolSnapshot(), node => node.Name == "optional").Status);
            await McpCredentialTestEnvironment.AssertTurnCompletesAsync(chat);
            Assert.Equal(new[] { "healthy_tool", "optional_tool" },
                client.LastRequestOptions!.Tools!.Select(tool => tool.Name).Order().ToArray());
            foreach (var result in environment.Results)
                Assert.IsType<SensitiveValueUseSucceeded<bool>>(await result.Lease.UseAsync(
                    (_, _) => ValueTask.FromResult(true), CancellationToken.None));
        }
        finally { await chat.DisposeAsync(); }
        foreach (var result in environment.Results)
            Assert.IsType<SensitiveValueUseFailed<bool>>(await result.Lease.UseAsync(
                (_, _) => ValueTask.FromResult(true), CancellationToken.None));
        Assert.All(environment.TransportDisposals.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task TerminationDuringAuthorization_DrainsPendingRequestAndReleasesCoreAndSiblingScopes()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var environment = new McpCredentialTestEnvironment();
        environment.Acquire = async (request, cancellation) =>
        {
            if (request.Reference.Name != "Optional")
                return environment.Success();
            entered.TrySetResult();
            try
            {
                await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(cancellation);
                throw new InvalidOperationException();
            }
            finally { cancelled.TrySetResult(); }
        };
        var client = McpCredentialTestEnvironment.Client();
        await using var factory = new AgentChatFactory(
            new InMemoryAgentPersistenceStore(), environment.Services(client), TaskScheduler.Default);
        var identifier = new AgentSessionId("pending-lifetime");
        var backgroundOwner = await factory.GetOrCreateAsync(
            identifier, McpCredentialTestEnvironment.Definition(required: true, sibling: true));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await WaitForServerAsync(backgroundOwner.LocalAgentChat, "healthy", node => node.Children.Count == 1);
        var viewer = await factory.GetAsync(identifier);
        await viewer.DisposeAsync();
        Assert.False(cancelled.Task.IsCompleted);
        Assert.Single(factory.RunningSessions);
        Assert.True(await factory.TerminateAsync(identifier, CancellationToken.None));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Empty(factory.RunningSessions);
        await backgroundOwner.DisposeAsync();
        foreach (var result in environment.Results)
            Assert.IsType<SensitiveValueUseFailed<bool>>(await result.Lease.UseAsync(
                (_, _) => ValueTask.FromResult(true), CancellationToken.None));
    }

    private static Task<AgentChat> CreateAsync(
        McpCredentialTestEnvironment environment,
        DeterministicTestChatClient client,
        bool required = false,
        bool sibling = false)
        => AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = McpCredentialTestEnvironment.Definition(required, sibling),
            AgentServices = environment.Services(client),
            PersistenceStoreFactory = (_, _) => ValueTask.FromResult<IAgentPersistenceStore>(new InMemoryAgentPersistenceStore()),
        });

    private static async Task WaitForServerAsync(
        AgentChat chat,
        string name,
        Func<AgentChatToolItem, bool> predicate)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(object? sender, EventArgs change)
        {
            if (chat.GetToolSnapshot().Any(node => node.Name == name && predicate(node)))
                ready.TrySetResult();
        }
        chat.ToolsChanged += Check;
        try
        {
            Check(null, EventArgs.Empty);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally { chat.ToolsChanged -= Check; }
    }
}
