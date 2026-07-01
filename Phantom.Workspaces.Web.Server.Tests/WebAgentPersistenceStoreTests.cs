using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using Phantom.Workspaces.Data.Web.Client;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Web.Server.Tests;

public sealed class WebAgentPersistenceStoreTests : IAsyncLifetime
{
    private readonly InMemoryAgentPersistenceStore backingStore = new();
    private WebApplication? app;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IAgentPersistenceStore>(backingStore);
        app = builder.Build();
        app.MapAgentPersistenceEndpoints();
        await app.StartAsync();
        backingStore.Reset();
    }

    public async Task DisposeAsync()
    {
        if (app is not null)
        {
            await app.DisposeAsync();
        }
    }

    private WebAgentPersistenceStore CreateClient() =>
        new(app!.GetTestServer().CreateClient());

    [Fact]
    public async Task StoreAsync_WhenServerReturns503_ThrowsHttpRequestException()
    {
        var serverBuilder = WebApplication.CreateBuilder();
        serverBuilder.WebHost.UseTestServer();
        await using var serverWithoutStore = serverBuilder.Build();
        serverWithoutStore.MapAgentPersistenceEndpoints();
        await serverWithoutStore.StartAsync();

        var store = new WebAgentPersistenceStore(serverWithoutStore.GetTestServer().CreateClient());
        var agent = CreateAgent("session-503");

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            store.StoreAsync(
                new StoreRequestAgent { Agent = agent },
                CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task RestoreAsync_WhenServerReturns404_ReturnsNull()
    {
        var store = CreateClient();

        var result = await store.RestoreAsync(
            new RestoreRequest { AgentSessionId = "session-not-found" },
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task StoreAsync_PreservesBsonDocumentRoundTrip()
    {
        var store = CreateClient();
        var agentDefinition = BsonDocument.Parse(
            """
            {
              "kind": "prompt",
              "name": "round-trip-agent",
              "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
              "tools": []
            }
            """);
        var agentSession = BsonDocument.Parse("""{ "session-id": "round-trip-session" }""");

        var persistedAgent = new PersistedAgent
        {
            AgentSessionId = "session-round-trip",
            AgentDefinitionJson = agentDefinition,
            AgentSessionJson = agentSession,
        };

        await store.StoreAsync(
            new StoreRequestAgent { Agent = persistedAgent },
            CancellationToken.None);

        var restored = await store.RestoreAsync(
            new RestoreRequest { AgentSessionId = "session-round-trip" },
            CancellationToken.None);

        Assert.NotNull(restored);
        Assert.Equal(agentDefinition.ToJson(), restored.Value.AgentDefinitionJson!.ToJson());
        Assert.Equal(agentSession.ToJson(), restored.Value.AgentSessionJson!.ToJson());
    }

    [Fact]
    public async Task StoreAsync_WithNullMessages_DoesNotThrow()
    {
        var store = CreateClient();
        var agent = CreateAgent("session-null-messages");

        await store.StoreAsync(
            new StoreRequestAgent { Agent = agent, NewMessages = null },
            CancellationToken.None);

        var messages = await store.ReadMessagesAsync(
            new ReadMessagesRequest { AgentSessionId = "session-null-messages" },
            CancellationToken.None);

        Assert.Empty(messages);
    }

    [Fact]
    public async Task ReadMessagesAsync_WithComplexMessageContents_RoundTrip()
    {
        var store = CreateClient();
        var agent = CreateAgent("session-complex-messages");

        var toolCall = new FunctionCallContent("call-1", "myTool", new Dictionary<string, object?>
        {
            ["param"] = "value",
        });
        var toolResult = new FunctionResultContent("call-1", "result-value");

        var messages = new[]
        {
            new ChatMessage(ChatRole.Assistant, [toolCall]),
            new ChatMessage(ChatRole.Tool, [toolResult]),
        };

        await store.StoreAsync(
            new StoreRequestAgent { Agent = agent, NewMessages = messages },
            CancellationToken.None);

        var restored = await store.ReadMessagesAsync(
            new ReadMessagesRequest { AgentSessionId = "session-complex-messages" },
            CancellationToken.None);

        Assert.Equal(2, restored.Length);
        var restoredToolCall = Assert.Single(restored[0].Contents.OfType<FunctionCallContent>());
        Assert.Equal("call-1", restoredToolCall.CallId);
        Assert.Equal("myTool", restoredToolCall.Name);
        var restoredToolResult = Assert.Single(restored[1].Contents.OfType<FunctionResultContent>());
        Assert.Equal("call-1", restoredToolResult.CallId);
    }

    private static PersistedAgent CreateAgent(string sessionId) => new()
    {
        AgentSessionId = sessionId,
        AgentDefinitionJson = BsonDocument.Parse("""{ "name": "test" }"""),
    };
}
