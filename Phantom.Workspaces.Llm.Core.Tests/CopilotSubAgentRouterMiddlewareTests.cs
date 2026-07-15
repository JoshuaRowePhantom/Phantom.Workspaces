using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AgentSchema;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Xunit;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Tests for <see cref="CopilotSubAgentRouterMiddleware"/>: the <see cref="IChatClient"/> middleware
/// seam that pipes the inner client's translated update stream through
/// <see cref="CopilotSubAgentRouter"/>. Drives the middleware with pre-recorded
/// <see cref="ChatResponseUpdate"/> streams (no raw Copilot SDK event types) and verifies sub-agent
/// creation, receiver routing, root pass-through, and lease teardown.
/// </summary>
public sealed class CopilotSubAgentRouterMiddlewareTests
{
    // ─── update builders (mirror the annotated stream CopilotSdkStreamAdapter emits) ────────

    private static ChatResponseUpdate RootText(string text) =>
        new() { Role = ChatRole.Assistant, Contents = [new TextContent(text)] };

    private static ChatResponseUpdate SubAgentText(string agentId, string text)
    {
        var content = new TextContent(text)
        {
            AdditionalProperties = new()
            {
                [CopilotSdkStreamAdapter.ParentToolCallIdPropertyName] = agentId,
            },
        };
        return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [content] };
    }

    private static ChatResponseUpdate LifecycleStart(
        string agentId,
        string parentToolCallId,
        string displayName = "Sub Agent",
        string description = "desc")
    {
        var call = new FunctionCallContent(
            agentId,
            CopilotSdkStreamAdapter.SubAgentStartLifecycleName,
            new Dictionary<string, object?>
            {
                [CopilotSdkStreamAdapter.ParentToolCallIdArgumentName] = parentToolCallId,
                [CopilotSdkStreamAdapter.DisplayNameArgumentName] = displayName,
                [CopilotSdkStreamAdapter.DescriptionArgumentName] = description,
            })
        {
            AdditionalProperties = new()
            {
                [CopilotSdkStreamAdapter.ContentTypePropertyName] = CopilotSdkStreamAdapter.SubAgentLifecycleContentType,
            },
        };
        return new ChatResponseUpdate { Contents = [call] };
    }

    private static ChatResponseUpdate LifecycleCompleted(string agentId)
    {
        var result = new FunctionResultContent(agentId, """{"event":"completed"}""")
        {
            AdditionalProperties = new()
            {
                [CopilotSdkStreamAdapter.ContentTypePropertyName] = CopilotSdkStreamAdapter.SubAgentLifecycleContentType,
            },
        };
        return new ChatResponseUpdate { Contents = [result] };
    }

    private static async Task<List<ChatResponseUpdate>> RunMiddlewareAsync(
        CopilotSubAgentRouterMiddleware middleware)
    {
        var rootUpdates = new List<ChatResponseUpdate>();
        await foreach (var update in middleware.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "go")], null, CancellationToken.None))
        {
            rootUpdates.Add(update);
        }

        return rootUpdates;
    }

    private static async Task<List<ChatResponseUpdate>> DrainReceiverAsync(CopilotSubAgentChatClient receiver)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in receiver.GetStreamingResponseAsync([]))
        {
            updates.Add(update);
        }

        return updates;
    }

    // ─── tests ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CopilotSubAgentRouterMiddleware_RootUpdates_PassThrough()
    {
        var inner = new ScriptedChatClient([RootText("hello"), RootText(" world")]);
        var factory = new FakeRunningAgentChatFactory();
        var table = new FakeSubAgentTable();
        using var middleware = new CopilotSubAgentRouterMiddleware(inner, factory, table);

        var rootUpdates = await RunMiddlewareAsync(middleware);

        Assert.Equal(2, rootUpdates.Count);
        Assert.Equal("hello", ((TextContent)rootUpdates[0].Contents.Single()).Text);
        Assert.Empty(factory.CreateCalls);
    }

    [Fact]
    public async Task CopilotSubAgentRouterMiddleware_SubAgentStart_CreatesAgentChat()
    {
        var inner = new ScriptedChatClient([LifecycleStart("agent-1", "call-1")]);
        var factory = new FakeRunningAgentChatFactory();
        var table = new FakeSubAgentTable();
        using var middleware = new CopilotSubAgentRouterMiddleware(inner, factory, table);

        var rootUpdates = await RunMiddlewareAsync(middleware);

        // Lifecycle signals are interpreted, not forwarded to the root stream.
        Assert.Empty(rootUpdates);
        var (definition, _) = Assert.Single(factory.CreateCalls);
        Assert.Contains("github-copilot-subagent", definition.ToJson());
        var added = Assert.Single(table.AddedChats);
        Assert.Same(factory.CreatedLease!.AgentChat, added);
    }

    [Fact]
    public async Task CopilotSubAgentRouterMiddleware_SubAgentUpdates_RoutedToReceiver()
    {
        var inner = new ScriptedChatClient(
        [
            LifecycleStart("agent-1", "call-1"),
            SubAgentText("agent-1", "hello from sub-agent"),
            LifecycleCompleted("agent-1"),
        ]);
        var factory = new FakeRunningAgentChatFactory();
        var table = new FakeSubAgentTable();
        using var middleware = new CopilotSubAgentRouterMiddleware(inner, factory, table);

        var rootUpdates = await RunMiddlewareAsync(middleware);

        Assert.Empty(rootUpdates);
        var receiver = (CopilotSubAgentChatClient)factory.CreatedLease!.AgentChat
            .GetService(typeof(ICopilotSubAgentReceiver))!;
        var routed = await DrainReceiverAsync(receiver);
        Assert.Contains(routed, u => u.Text == "hello from sub-agent");
    }

    [Fact]
    public async Task CopilotSubAgentRouterMiddleware_IncompleteSubAgent_FailsReceiverOnDispose()
    {
        var inner = new ScriptedChatClient([LifecycleStart("agent-1", "call-1")]);
        var factory = new FakeRunningAgentChatFactory();
        var table = new FakeSubAgentTable();
        using var middleware = new CopilotSubAgentRouterMiddleware(inner, factory, table);

        // The sub-agent never completes; enumerating to the end triggers the middleware's finally
        // block, which disposes remaining leases and faults their receivers.
        await RunMiddlewareAsync(middleware);

        var receiver = (CopilotSubAgentChatClient)factory.CreatedLease!.AgentChat
            .GetService(typeof(ICopilotSubAgentReceiver))!;
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var _ in receiver.GetStreamingResponseAsync([]))
            {
            }
        });
        Assert.True(factory.LeaseDisposed);
    }

    // ─── fakes ──────────────────────────────────────────────────────────────────

    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly IReadOnlyList<ChatResponseUpdate> updates;

        public ScriptedChatClient(IReadOnlyList<ChatResponseUpdate> updates) => this.updates = updates;

        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in this.updates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
            }

            await Task.CompletedTask;
        }
    }

    private sealed class FakeRunningAgentChatFactory : IRunningAgentChatFactory
    {
        public List<(AgentDefinition Definition, AgentSessionId SessionId)> CreateCalls { get; } = new();
        public RunningAgentChatLease? CreatedLease { get; private set; }
        public bool LeaseDisposed { get; private set; }

        public System.Collections.ObjectModel.ObservableCollection<RunningAgentChat> RunningSessions { get; } = new();

        Task<RunningAgentChatLease> IRunningAgentChatFactory.CreateAsync(
            AgentDefinition definition,
            AgentSessionId sessionId,
            AgentServices? services,
            CancellationToken ct)
        {
            CreateCalls.Add((definition, sessionId));

            var receiver = new CopilotSubAgentChatClient();

            // Create AgentChat via the internal constructor, skipping CreateAsync/InitializeAsync to
            // avoid starting the background processing loop. UseProvidedChatClientAsIs = true keeps
            // the client unwrapped so GetService(typeof(ICopilotSubAgentReceiver)) returns the exact
            // CopilotSubAgentChatClient the router pushes updates to.
            var chat = new AgentChat(new InternalCreateAgentChatRequest
            {
                AgentDefinition = null,
                ConfiguredStore = new InMemoryAgentPersistenceStore(),
            });

            var chatClientAgent = new ChatClientAgent(receiver, new ChatClientAgentOptions
            {
                UseProvidedChatClientAsIs = true,
            });
            var chatClientAgentField = typeof(AgentChat).GetField(
                "chatClientAgent",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            chatClientAgentField!.SetValue(chat, chatClientAgent);

            var lease = new RunningAgentChatLease(sessionId, chat, () =>
            {
                LeaseDisposed = true;
                return ValueTask.CompletedTask;
            });
            CreatedLease = lease;
            return Task.FromResult(lease);
        }

        Task<RunningAgentChatLease> IRunningAgentChatFactory.GetAsync(AgentSessionId sessionId, CancellationToken ct) =>
            throw new NotImplementedException();

        Task<RunningAgentChatLease> IRunningAgentChatFactory.GetOrCreateAsync(
            AgentSessionId sessionId,
            AgentDefinition? definition,
            AgentServices? services,
            string? displayNameOverride,
            string? descriptionOverride,
            CancellationToken ct) =>
            throw new NotImplementedException();
    }

    private sealed class FakeSubAgentTable : ISubAgentTable
    {
        public List<AgentChat> AddedChats { get; } = new();

        Task<SubAgent> ISubAgentTable.Add(AgentChat agentChat)
        {
            AddedChats.Add(agentChat);
            var sessionId = new AgentSessionId(agentChat.AgentSessionId);
            return Task.FromResult(new SubAgent(sessionId, agentChat, null));
        }
    }
}
