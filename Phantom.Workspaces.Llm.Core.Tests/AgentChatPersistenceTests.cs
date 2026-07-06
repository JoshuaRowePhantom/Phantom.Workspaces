using AgentSchema;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Interfaces;
using Xunit;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class AgentChatPersistenceTests
{
    private static readonly string ParentAgentDefinitionJson =
        """
        {
          "kind": "prompt",
          "name": "parent-agent",
          "model": {
            "id": "echo",
            "provider": "echo",
            "apiType": "Echo"
          },
          "tools": []
        }
        """;

    private static readonly string SubAgentDefinitionJson =
        """
        {
          "kind": "prompt",
          "name": "sub-agent",
          "model": {
            "id": "echo",
            "provider": "echo",
            "apiType": "Echo"
          },
          "tools": []
        }
        """;

    private static AgentDefinition ParentDefinition =>
        AgentDefinitionLoader.LoadAgentFromJson(ParentAgentDefinitionJson);

    private static AgentDefinition SubDefinition =>
        AgentDefinitionLoader.LoadAgentFromJson(SubAgentDefinitionJson);

    private static async Task<AgentChat> CreateParentChatAsync(
        InMemoryAgentPersistenceStore store,
        string? agentSessionId = null) =>
        await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = ParentDefinition,
            AgentSessionId = agentSessionId,
            ConfiguredStore = store,
            ClientOverride = new DeterministicTestChatClient(),
            DisplayNameOverride = "parent",
        });

    [Fact]
    public async Task GetOrCreateAsync_AddsSubAgentLink()
    {
        var store = new InMemoryAgentPersistenceStore();
        await using var parent = await CreateParentChatAsync(store);

        await parent.GetOrCreateAsync("agent-1", SubDefinition, "tool-call-1");

        var childIds = await store.ReadSubAgentChildIdsAsync(parent.AgentSessionId);
        Assert.Single(childIds);
    }

    [Fact]
    public async Task InitializeAsync_RestoresSubAgents_FromManifest()
    {
        var store = new InMemoryAgentPersistenceStore();
        string parentSessionId;

        await using (var parent = await CreateParentChatAsync(store))
        {
            var sink = (ISubAgentChat)await parent.GetOrCreateAsync("agent-1", SubDefinition, "tool-call-1");
            sink.Complete();
            await Task.Yield();
            parentSessionId = parent.AgentSessionId;
        }

        await using var restoredParent = await CreateParentChatAsync(store, parentSessionId);

        Assert.Single(restoredParent.SubAgents);
    }

    [Fact]
    public async Task InitializeAsync_RestoredSubAgent_HasCorrectAgentDefinition()
    {
        var store = new InMemoryAgentPersistenceStore();
        string parentSessionId;

        await using (var parent = await CreateParentChatAsync(store))
        {
            var sink = (ISubAgentChat)await parent.GetOrCreateAsync("agent-1", SubDefinition, "tool-call-1");
            sink.Complete();
            await Task.Yield();
            parentSessionId = parent.AgentSessionId;
        }

        await using var restoredParent = await CreateParentChatAsync(store, parentSessionId);

        var restoredChild = (AgentChat)Assert.Single(restoredParent.SubAgents);
        Assert.Equal("sub-agent", restoredChild.AgentDefinition?.Name);
    }

    [Fact]
    public async Task InitializeAsync_RestoredSubAgent_ChatHistoryLoaded()
    {
        var store = new InMemoryAgentPersistenceStore();
        string parentSessionId;
        string childSessionId;

        await using (var parent = await CreateParentChatAsync(store))
        {
            var sink = (ISubAgentChat)await parent.GetOrCreateAsync("agent-1", SubDefinition, "tool-call-1");
            childSessionId = ((AgentChat)Assert.Single(parent.SubAgents)).AgentSessionId;

            // Write a message into the child's session in the store
            await store.StoreAsync(new StoreRequestAgent
            {
                Agent = new PersistedAgent { AgentSessionId = childSessionId },
                NewMessages = [new ChatMessage(ChatRole.User, "hello from history")],
            });

            sink.Complete();
            await Task.Yield();
            parentSessionId = parent.AgentSessionId;
        }

        await using var restoredParent = await CreateParentChatAsync(store, parentSessionId);

        var restoredChild = (AgentChat)Assert.Single(restoredParent.SubAgents);
        Assert.True(restoredChild.History.Count > 0);
    }

    [Fact]
    public async Task InitializeAsync_RestoredSubAgent_IsHostedAgent_AcceptsUserInput_False()
    {
        var store = new InMemoryAgentPersistenceStore();
        string parentSessionId;

        await using (var parent = await CreateParentChatAsync(store))
        {
            var sink = (ISubAgentChat)await parent.GetOrCreateAsync("agent-1", SubDefinition, "tool-call-1");
            sink.Complete();
            await Task.Yield();
            parentSessionId = parent.AgentSessionId;
        }

        await using var restoredParent = await CreateParentChatAsync(store, parentSessionId);

        var restoredChild = (AgentChat)Assert.Single(restoredParent.SubAgents);
        Assert.False(restoredChild.AcceptsUserInput);
    }
}
