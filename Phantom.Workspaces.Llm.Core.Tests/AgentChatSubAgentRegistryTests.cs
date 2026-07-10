using AgentSchema;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Interfaces;
using Xunit;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class AgentChatSubAgentRegistryTests
{
    private static readonly string DefaultAgentDefinitionJson =
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

    private static AgentChat CreateParentChat() =>
        AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(DefaultAgentDefinitionJson),
            ConfiguredStore = new InMemoryAgentPersistenceStore(),
            ClientOverride = new DeterministicTestChatClient(),
            DisplayNameOverride = "parent-chat",
        }).GetAwaiter().GetResult();

    private static AgentDefinition CreateSubAgentDefinition(string name = "sub-agent") =>
        AgentDefinitionLoader.LoadAgentFromJson($$"""
            {
              "kind": "prompt",
              "name": "{{name}}",
              "model": {
                "id": "echo",
                "provider": "echo",
                "apiType": "Echo"
              },
              "tools": []
            }
            """);

    [Fact]
    public async Task GetOrCreateAsync_AddsChildToSubAgents()
    {
        await using var parent = CreateParentChat();
        var subDef = CreateSubAgentDefinition();

        await parent.GetOrCreateAsync("agent-1", subDef, "tool-call-1");

        Assert.Single(parent.SubAgents);
    }

    [Fact]
    public async Task GetOrCreateAsync_SameAgentId_ReturnsSameChild()
    {
        await using var parent = CreateParentChat();
        var subDef = CreateSubAgentDefinition();

        var first = await parent.GetOrCreateAsync("agent-1", subDef, "tool-call-1");
        var second = await parent.GetOrCreateAsync("agent-1", subDef, "tool-call-1");

        Assert.Same(first, second);
        Assert.Single(parent.SubAgents);
    }

    [Fact]
    public async Task TryGet_UnknownAgentId_ReturnsNull()
    {
        await using var parent = CreateParentChat();

        var result = parent.TryGet("nonexistent");

        Assert.Null(result);
    }

    [Fact]
    public async Task TryGet_KnownAgentId_ReturnsSink()
    {
        await using var parent = CreateParentChat();
        var subDef = CreateSubAgentDefinition();

        var sink = await parent.GetOrCreateAsync("agent-1", subDef, "tool-call-1");

        Assert.Same(sink, parent.TryGet("agent-1"));
    }

    [Fact]
    public async Task AcceptsUserInput_True_WhenChatClientIsNormal()
    {
        await using var chat = CreateParentChat();
        Assert.True(chat.AcceptsUserInput);
    }

    [Fact]
    public async Task AcceptsUserInput_False_WhenChatClientIsIHostedAgentChatClient()
    {
        await using var parent = CreateParentChat();
        var subDef = CreateSubAgentDefinition();
        await parent.GetOrCreateAsync("agent-1", subDef, "tool-call-1");

        var child = Assert.Single(parent.SubAgents);

        Assert.False(((AgentChat)child).AcceptsUserInput);
    }

    [Fact]
    public async Task CompletionState_Running_Initially()
    {
        await using var parent = CreateParentChat();
        var subDef = CreateSubAgentDefinition();

        await parent.GetOrCreateAsync("agent-1", subDef, "tool-call-1");

        var child = Assert.Single(parent.SubAgents);
        Assert.Equal(AgentChatCompletionState.Running, child.CompletionState);
    }

    [Fact]
    public async Task CompletionState_Succeeded_WhenComplete_Called()
    {
        await using var parent = CreateParentChat();
        var subDef = CreateSubAgentDefinition();

        var sink = await parent.GetOrCreateAsync("agent-1", subDef, "tool-call-1");
        var child = Assert.Single(parent.SubAgents);

        sink.Complete();

        Assert.Equal(AgentChatCompletionState.Succeeded, child.CompletionState);
    }

    [Fact]
    public async Task CompletionState_Failed_WhenFail_Called()
    {
        await using var parent = CreateParentChat();
        var subDef = CreateSubAgentDefinition();

        var sink = await parent.GetOrCreateAsync("agent-1", subDef, "tool-call-1");
        var child = Assert.Single(parent.SubAgents);

        sink.Fail(new InvalidOperationException("test failure"));

        Assert.Equal(AgentChatCompletionState.Failed, child.CompletionState);
    }

    [Fact]
    public async Task ParentAgent_Set_OnChildAgentChat()
    {
        await using var parent = CreateParentChat();
        var subDef = CreateSubAgentDefinition();

        await parent.GetOrCreateAsync("agent-1", subDef, "tool-call-1");

        var child = (AgentChat)Assert.Single(parent.SubAgents);
        Assert.Same(parent, child.ParentAgent);
    }

    [Fact]
    public async Task GetOrCreateAsync_TwiceSameId_OneChildCreated()
    {
        await using var parent = CreateParentChat();
        var subDef = CreateSubAgentDefinition();

        await parent.GetOrCreateAsync("agent-1", subDef, "tool-call-1");
        await parent.GetOrCreateAsync("agent-1", subDef, "tool-call-2");

        Assert.Single(parent.SubAgents);
    }

    [Fact]
    public async Task GetOrCreateAsync_AgentId_SetOnChild()
    {
        await using var parent = CreateParentChat();
        var subDef = CreateSubAgentDefinition();

        await parent.GetOrCreateAsync("my-agent-id", subDef, "tool-call-1");

        var child = (AgentChat)Assert.Single(parent.SubAgents);
        Assert.Equal("my-agent-id", child.AgentId);
    }

    [Fact]
    public async Task ISubAgentTable_Add_PersistsLinkBeforeReturning()
    {
        var store = new InMemoryAgentPersistenceStore();
        var parentChat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(DefaultAgentDefinitionJson),
            ConfiguredStore = store,
            ClientOverride = new DeterministicTestChatClient(),
            DisplayNameOverride = "parent-chat",
        });

        var childChat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = CreateSubAgentDefinition(),
            ConfiguredStore = store,
            ClientOverride = new DeterministicTestChatClient(),
            DisplayNameOverride = "child-chat",
        });

        await using var parentDispose = parentChat;
        await using var childDispose = childChat;

        // Act: Add the child to the parent
        await ((ISubAgentTable)parentChat).Add(childChat);

        // Assert: The link should be immediately readable from the store
        var childIds = await store.ReadSubAgentChildIdsAsync(parentChat.AgentSessionId);
        var childId = Assert.Single(childIds);
        Assert.Equal(childChat.AgentSessionId, childId.Value);
    }
}
