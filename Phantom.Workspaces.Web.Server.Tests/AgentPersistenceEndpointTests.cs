using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using Phantom.Workspaces.Data.Web.Client;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Web.Server.Tests;

public sealed class AgentPersistenceEndpointTests : IAsyncLifetime
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

    [Fact]
    public void MapAgentPersistenceEndpoints_MapsAllThreeRoutes()
    {
        var builder = WebApplication.CreateBuilder();
        var testApp = builder.Build();

        testApp.MapAgentPersistenceEndpoints();

        var routePatterns = ((IEndpointRouteBuilder)testApp).DataSources
            .SelectMany(static ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(static ep => ep.RoutePattern.RawText)
            .ToArray();

        Assert.Contains("/agent/persistence/store", routePatterns);
        Assert.Contains("/agent/persistence/restore", routePatterns);
        Assert.Contains("/agent/persistence/messages", routePatterns);
    }

    [Fact]
    public async Task Store_WithValidRequest_Returns200()
    {
        using var client = app!.GetTestServer().CreateClient();

        var request = new StoreAgentRequest
        {
            Agent = new PersistedAgentDto
            {
                AgentSessionId = "session-store-ok",
                AgentDefinitionJson = JsonDocument.Parse("""{ "name": "test" }""").RootElement,
            },
        };

        using var response = await client.PostAsJsonAsync(
            "/agent/persistence/store", request, AIJsonUtilities.DefaultOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Store_WithMalformedBody_Returns400()
    {
        using var client = app!.GetTestServer().CreateClient();
        using var content = new StringContent("not-json", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/agent/persistence/store", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Store_WhenStoreNotRegistered_Returns503()
    {
        var serverBuilder = WebApplication.CreateBuilder();
        serverBuilder.WebHost.UseTestServer();
        await using var serverWithoutStore = serverBuilder.Build();
        serverWithoutStore.MapAgentPersistenceEndpoints();
        await serverWithoutStore.StartAsync();

        using var clientWithoutStore = serverWithoutStore.GetTestServer().CreateClient();
        var request = new StoreAgentRequest
        {
            Agent = new PersistedAgentDto { AgentSessionId = "session-503" },
        };

        using var response = await clientWithoutStore.PostAsJsonAsync(
            "/agent/persistence/store", request, AIJsonUtilities.DefaultOptions);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Restore_WhenSessionExists_Returns200WithDataTransferObject()
    {
        using var client = app!.GetTestServer().CreateClient();

        var agentDefinition = BsonDocument.Parse("""{ "kind": "prompt", "name": "agent-restore-ok" }""");
        await backingStore.StoreAsync(new StoreRequestAgent
        {
            Agent = new PersistedAgent
            {
                AgentSessionId = "session-restore-ok",
                AgentDefinitionJson = agentDefinition,
            },
        });

        using var response = await client.PostAsJsonAsync(
            "/agent/persistence/restore",
            new { agentSessionId = "session-restore-ok" },
            AIJsonUtilities.DefaultOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var dto = await response.Content.ReadFromJsonAsync<PersistedAgentDto>(AIJsonUtilities.DefaultOptions);
        Assert.NotNull(dto);
        Assert.Equal("session-restore-ok", dto.AgentSessionId);
        Assert.NotNull(dto.AgentDefinitionJson);
    }

    [Fact]
    public async Task Restore_WhenSessionMissing_Returns404()
    {
        using var client = app!.GetTestServer().CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/agent/persistence/restore",
            new { agentSessionId = "session-missing" },
            AIJsonUtilities.DefaultOptions);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Messages_WhenSessionExists_ReturnsOrderedMessages()
    {
        using var client = app!.GetTestServer().CreateClient();

        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "first"),
            new ChatMessage(ChatRole.Assistant, "second"),
            new ChatMessage(ChatRole.User, "third"),
        };

        await backingStore.StoreAsync(new StoreRequestAgent
        {
            Agent = new PersistedAgent { AgentSessionId = "session-messages-ok" },
            NewMessages = messages,
        });

        using var response = await client.PostAsJsonAsync(
            "/agent/persistence/messages",
            new { agentSessionId = "session-messages-ok" },
            AIJsonUtilities.DefaultOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<ReadMessagesResponse>(AIJsonUtilities.DefaultOptions);
        Assert.NotNull(result);
        Assert.Equal(3, result.Messages.Length);
        Assert.Equal("first", result.Messages[0].Text);
        Assert.Equal("second", result.Messages[1].Text);
        Assert.Equal("third", result.Messages[2].Text);
    }
}
