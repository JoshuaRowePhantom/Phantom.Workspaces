using AgentSchema;
using Microsoft.Extensions.AI;
using MongoDB.Bson;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm.Tests;

public abstract class AgentPersistenceStoreContractTests
{
    protected abstract ValueTask<IAgentPersistenceStore> CreateStoreAsync();

    protected virtual ValueTask ResetStoreAsync()
    {
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task StoreAsync_ThenReadMessagesAsync_ReturnsStoredMessagesInOrder()
    {
        await this.ResetStoreAsync();
        var store = await this.CreateStoreAsync();
        var persistedAgent = CreatePersistedAgent("session-contract-1", "contract-agent-1");
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "hello"),
            new ChatMessage(ChatRole.Assistant, "world"),
        };

        await store.StoreAsync(
            new StoreRequestAgent
            {
                Agent = persistedAgent,
                NewMessages = messages,
            },
            CancellationToken.None);

        var restoredMessages = await store.ReadMessagesAsync(
            new ReadMessagesRequest { AgentSessionId = persistedAgent.AgentSessionId },
            CancellationToken.None);

        Assert.Equal(2, restoredMessages.Length);
        Assert.Equal("hello", Assert.Single(restoredMessages[0].Contents.OfType<TextContent>()).Text);
        Assert.Equal("world", Assert.Single(restoredMessages[1].Contents.OfType<TextContent>()).Text);
    }

    [Fact]
    public async Task StoreAsync_CalledTwice_AppendsMessages()
    {
        await this.ResetStoreAsync();
        var store = await this.CreateStoreAsync();
        var persistedAgent = CreatePersistedAgent("session-contract-2", "contract-agent-2");

        await store.StoreAsync(
            new StoreRequestAgent
            {
                Agent = persistedAgent,
                NewMessages = [new ChatMessage(ChatRole.User, "first")],
            },
            CancellationToken.None);
        await store.StoreAsync(
            new StoreRequestAgent
            {
                Agent = persistedAgent,
                NewMessages = [new ChatMessage(ChatRole.Assistant, "second")],
            },
            CancellationToken.None);

        var restoredMessages = await store.ReadMessagesAsync(
            new ReadMessagesRequest { AgentSessionId = persistedAgent.AgentSessionId },
            CancellationToken.None);

        Assert.Equal(2, restoredMessages.Length);
        Assert.Equal("first", Assert.Single(restoredMessages[0].Contents.OfType<TextContent>()).Text);
        Assert.Equal("second", Assert.Single(restoredMessages[1].Contents.OfType<TextContent>()).Text);
    }

    [Fact]
    public async Task StoreAsync_ThenRestoreAsync_ReturnsPersistedAgent()
    {
        await this.ResetStoreAsync();
        var store = await this.CreateStoreAsync();
        var persistedAgent = CreatePersistedAgent("session-contract-3", "contract-agent-3");

        await store.StoreAsync(
            new StoreRequestAgent
            {
                Agent = persistedAgent,
                NewMessages = [],
            },
            CancellationToken.None);

        var restoredAgent = await store.RestoreAsync(
            new RestoreRequest { AgentSessionId = persistedAgent.AgentSessionId },
            CancellationToken.None);

        Assert.NotNull(restoredAgent);
        Assert.Equal(persistedAgent.AgentSessionId, restoredAgent.Value.AgentSessionId);
        Assert.Equal(persistedAgent.AgentDefinitionJson!.ToJson(), restoredAgent.Value.AgentDefinitionJson!.ToJson());
    }

    [Fact]
    public async Task StoreAsync_WhenCalledWithoutMessages_StillPersistsAgent()
    {
        await this.ResetStoreAsync();
        var store = await this.CreateStoreAsync();
        var persistedAgent = CreatePersistedAgent("session-contract-4", "contract-agent-4");

        await store.StoreAsync(
            new StoreRequestAgent
            {
                Agent = persistedAgent,
            },
            CancellationToken.None);

        var restoredAgent = await store.RestoreAsync(
            new RestoreRequest { AgentSessionId = persistedAgent.AgentSessionId },
            CancellationToken.None);

        Assert.NotNull(restoredAgent);
        Assert.Equal(persistedAgent.AgentSessionId, restoredAgent.Value.AgentSessionId);
    }

    [Fact]
    public async Task ReadMessagesAsync_WhenSessionDoesNotExist_ReturnsEmpty()
    {
        await this.ResetStoreAsync();
        var store = await this.CreateStoreAsync();

        var restoredMessages = await store.ReadMessagesAsync(
            new ReadMessagesRequest { AgentSessionId = "does-not-exist" },
            CancellationToken.None);

        Assert.Empty(restoredMessages);
    }

    [Fact]
    public async Task RestoreAsync_WhenSessionDoesNotExist_ReturnsNull()
    {
        await this.ResetStoreAsync();
        var store = await this.CreateStoreAsync();

        var restoredAgent = await store.RestoreAsync(
            new RestoreRequest { AgentSessionId = "does-not-exist" },
            CancellationToken.None);

        Assert.Null(restoredAgent);
    }

    [Fact]
    public async Task StoreAsync_WhenDefinitionIsMissing_DoesNotClearStoredDefinition()
    {
        await this.ResetStoreAsync();
        var store = await this.CreateStoreAsync();
        var persistedAgent = CreatePersistedAgent("session-contract-5", "contract-agent-5");

        await store.StoreAsync(
            new StoreRequestAgent
            {
                Agent = persistedAgent,
            },
            CancellationToken.None);

        await store.StoreAsync(
            new StoreRequestAgent
            {
                Agent = persistedAgent with { AgentDefinitionJson = null },
                NewMessages = [new ChatMessage(ChatRole.User, "hello")],
            },
            CancellationToken.None);

        var restoredAgent = await store.RestoreAsync(
            new RestoreRequest { AgentSessionId = persistedAgent.AgentSessionId },
            CancellationToken.None);

        Assert.NotNull(restoredAgent);
        Assert.Equal(persistedAgent.AgentDefinitionJson!.ToJson(), restoredAgent.Value.AgentDefinitionJson!.ToJson());
    }

    [Fact]
    public async Task ReadBeforeStore_ForSession_ReturnsNoMessagesAndNoAgent()
    {
        await this.ResetStoreAsync();
        var store = await this.CreateStoreAsync();
        const string agentSessionId = "session-contract-not-yet-stored";

        var restoredMessages = await store.ReadMessagesAsync(
            new ReadMessagesRequest { AgentSessionId = agentSessionId },
            CancellationToken.None);
        var restoredAgent = await store.RestoreAsync(
            new RestoreRequest { AgentSessionId = agentSessionId },
            CancellationToken.None);

        Assert.Empty(restoredMessages);
        Assert.Null(restoredAgent);
    }

    private static PersistedAgent CreatePersistedAgent(string agentSessionId, string agentName)
    {
        return new PersistedAgent
        {
            AgentSessionId = agentSessionId,
            AgentDefinitionJson = BsonDocument.Parse(
                $$"""
                {
                  "kind": "prompt",
                  "name": "{{agentName}}",
                  "model": {
                    "id": "echo",
                    "provider": "echo",
                    "apiType": "Echo"
                  },
                  "tools": []
                }
                """),
            AgentSessionJson = BsonDocument.Parse(
                $$"""
                {
                  "session-id": "{{agentSessionId}}"
                }
                """),
        };
    }
}
