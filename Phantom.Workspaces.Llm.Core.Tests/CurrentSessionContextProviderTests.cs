using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Llm.Echo;
using System.Text.Json;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class CurrentSessionContextProviderTests
{
    private const string AgentSessionId = "session-1234";

    [Fact]
    public async Task GetCurrentSession_HappyPath_ReturnsAllFourMembers()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        await SeedAgentSessionAsync(dataAccessLayer, AgentSessionId, ["agent-sessions", "one"]);
        var profile = await SeedEntityAsync(dataAccessLayer, ["entity", "user-computer-profile"], ["profiles", "host-a"]);
        var user = await SeedEntityAsync(dataAccessLayer, ["entity", "user"], ["users", "alice"]);
        var definitionName = new EntityName("agent-definitions", "researcher");
        await SeedEntityAsync(dataAccessLayer, ["entity", "agent-definition"], ["agent-definitions", "researcher"]);

        var context = new CurrentSessionContext
        {
            AgentSessionId = AgentSessionId,
            UserComputerProfile = profile,
            User = user,
            AgentDefinitionReference = definitionName,
        };

        var result = await InvokeAsync(dataAccessLayer, context);

        Assert.Equal(AgentSessionId, ReadAgentSessionId(result.GetProperty("agent_session")));
        Assert.Equal(JsonValueKind.Object, result.GetProperty("user_computer_profile").ValueKind);
        Assert.Equal(JsonValueKind.Object, result.GetProperty("user").ValueKind);
        Assert.Equal(JsonValueKind.Object, result.GetProperty("agent_definition").ValueKind);
        Assert.Equal(
            "researcher",
            result.GetProperty("agent_definition").GetProperty("data").GetProperty("names")[0][1].GetString());
    }

    [Fact]
    public async Task GetCurrentSession_ResumeOnDifferentProfile_ReportsHostProfileNotSessionStored()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        await SeedAgentSessionAsync(dataAccessLayer, AgentSessionId, ["agent-sessions", "one"]);
        var firstProfile = await SeedEntityAsync(dataAccessLayer, ["entity", "user-computer-profile"], ["profiles", "host-a"]);
        var firstUser = await SeedEntityAsync(dataAccessLayer, ["entity", "user"], ["users", "alice"]);
        var secondProfile = await SeedEntityAsync(dataAccessLayer, ["entity", "user-computer-profile"], ["profiles", "host-b"]);
        var secondUser = await SeedEntityAsync(dataAccessLayer, ["entity", "user"], ["users", "bob"]);

        var firstHostResult = await InvokeAsync(
            dataAccessLayer,
            new CurrentSessionContext
            {
                AgentSessionId = AgentSessionId,
                UserComputerProfile = firstProfile,
                User = firstUser,
            });
        var secondHostResult = await InvokeAsync(
            dataAccessLayer,
            new CurrentSessionContext
            {
                AgentSessionId = AgentSessionId,
                UserComputerProfile = secondProfile,
                User = secondUser,
            });

        Assert.Equal("host-a", ReadFirstNameLeaf(firstHostResult.GetProperty("user_computer_profile")));
        Assert.Equal("alice", ReadFirstNameLeaf(firstHostResult.GetProperty("user")));
        Assert.Equal("host-b", ReadFirstNameLeaf(secondHostResult.GetProperty("user_computer_profile")));
        Assert.Equal("bob", ReadFirstNameLeaf(secondHostResult.GetProperty("user")));
    }

    [Fact]
    public async Task GetCurrentSession_IgnoresStaleHostProfileEntityId()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var staleProfile = await SeedEntityAsync(dataAccessLayer, ["entity", "user-computer-profile"], ["profiles", "stale-host"]);
        await SeedAgentSessionAsync(
            dataAccessLayer,
            AgentSessionId,
            ["agent-sessions", "one"],
            hostProfileEntityId: staleProfile.EntityId.ToString());
        var currentProfile = await SeedEntityAsync(dataAccessLayer, ["entity", "user-computer-profile"], ["profiles", "current-host"]);

        var result = await InvokeAsync(
            dataAccessLayer,
            new CurrentSessionContext
            {
                AgentSessionId = AgentSessionId,
                UserComputerProfile = currentProfile,
            });

        Assert.Equal("current-host", ReadFirstNameLeaf(result.GetProperty("user_computer_profile")));
    }

    [Fact]
    public async Task GetCurrentSession_NullProfileAndUser_MembersAreNull()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        await SeedAgentSessionAsync(dataAccessLayer, AgentSessionId, ["agent-sessions", "one"]);

        var result = await InvokeAsync(
            dataAccessLayer,
            new CurrentSessionContext { AgentSessionId = AgentSessionId });

        Assert.Equal(JsonValueKind.Null, result.GetProperty("user_computer_profile").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("user").ValueKind);
        Assert.Equal(JsonValueKind.Object, result.GetProperty("agent_session").ValueKind);
    }

    [Fact]
    public async Task GetCurrentSession_NoDefinitionReference_DefinitionNull()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        await SeedAgentSessionAsync(dataAccessLayer, AgentSessionId, ["agent-sessions", "one"]);

        var result = await InvokeAsync(
            dataAccessLayer,
            new CurrentSessionContext { AgentSessionId = AgentSessionId });

        Assert.Equal(JsonValueKind.Null, result.GetProperty("agent_definition").ValueKind);
    }

    [Fact]
    public async Task GetCurrentSession_UnknownSessionId_AgentSessionNull()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var profile = await SeedEntityAsync(dataAccessLayer, ["entity", "user-computer-profile"], ["profiles", "host-a"]);

        var result = await InvokeAsync(
            dataAccessLayer,
            new CurrentSessionContext
            {
                AgentSessionId = "no-such-session",
                UserComputerProfile = profile,
            });

        Assert.Equal(JsonValueKind.Null, result.GetProperty("agent_session").ValueKind);
        Assert.Equal(JsonValueKind.Object, result.GetProperty("user_computer_profile").ValueKind);
    }

    [Fact]
    public async Task GetCurrentSession_IncludeProfileFalse_OmitsProfileAndUser()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        await SeedAgentSessionAsync(dataAccessLayer, AgentSessionId, ["agent-sessions", "one"]);
        var profile = await SeedEntityAsync(dataAccessLayer, ["entity", "user-computer-profile"], ["profiles", "host-a"]);
        var user = await SeedEntityAsync(dataAccessLayer, ["entity", "user"], ["users", "alice"]);

        var result = await InvokeAsync(
            dataAccessLayer,
            new CurrentSessionContext
            {
                AgentSessionId = AgentSessionId,
                UserComputerProfile = profile,
                User = user,
            },
            new Dictionary<string, object?> { ["include_profile"] = false });

        Assert.Equal(JsonValueKind.Null, result.GetProperty("user_computer_profile").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("user").ValueKind);
        Assert.Equal(JsonValueKind.Object, result.GetProperty("agent_session").ValueKind);
    }

    [Fact]
    public async Task GetCurrentSession_IncludeDefinitionFalse_OmitsDefinition()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        await SeedAgentSessionAsync(dataAccessLayer, AgentSessionId, ["agent-sessions", "one"]);
        await SeedEntityAsync(dataAccessLayer, ["entity", "agent-definition"], ["agent-definitions", "researcher"]);

        var result = await InvokeAsync(
            dataAccessLayer,
            new CurrentSessionContext
            {
                AgentSessionId = AgentSessionId,
                AgentDefinitionReference = new EntityName("agent-definitions", "researcher"),
            },
            new Dictionary<string, object?> { ["include_definition"] = false });

        Assert.Equal(JsonValueKind.Null, result.GetProperty("agent_definition").ValueKind);
    }

    [Fact]
    public async Task GetCurrentSession_ToolMetadata_NameSchemaAndSingleTool()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var provider = new CurrentSessionContextProvider(
            dataAccessLayer,
            new CurrentSessionContext { AgentSessionId = AgentSessionId });

        var tools = await GetToolsAsync(provider);

        var tool = Assert.Single(tools);
        Assert.Equal("get_current_session", tool.Name);
        var schema = Assert.IsAssignableFrom<AIFunction>(tool).JsonSchema;
        Assert.Equal(JsonValueKind.Object, schema.GetProperty("properties").ValueKind);
        Assert.True(schema.GetProperty("properties").TryGetProperty("include_profile", out _));
        Assert.True(schema.GetProperty("properties").TryGetProperty("include_definition", out _));
    }

    [Fact]
    public async Task CreateCurrentSessionToolsetFactory_WhenKindMatches_ReturnsCurrentSessionProvider()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var factory = ToolsetFactory.CreateCurrentSessionToolsetFactory(
            dataAccessLayer,
            new CurrentSessionContext { AgentSessionId = AgentSessionId });

        var toolset = await factory.CreateToolsetAsync(
            new AgentSchema.CustomTool { Kind = "current-session", Name = "current-session" },
            new AgentServices());

        Assert.IsType<CurrentSessionContextProvider>(toolset);
    }

    [Fact]
    public async Task CreateCurrentSessionToolsetFactory_WhenKindDoesNotMatch_DefersToUnderlying()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var factory = ToolsetFactory.CreateCurrentSessionToolsetFactory(
            dataAccessLayer,
            new CurrentSessionContext { AgentSessionId = AgentSessionId });

        var toolset = await factory.CreateToolsetAsync(
            new AgentSchema.CustomTool { Kind = "web_search", Name = "web_search" },
            new AgentServices());

        Assert.Null(toolset);
    }

    [Fact]
    public async Task CreateCurrentSessionToolsetFactory_UsesContextCapturedInClosure()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        await SeedAgentSessionAsync(dataAccessLayer, AgentSessionId, ["agent-sessions", "one"]);
        var profile = await SeedEntityAsync(dataAccessLayer, ["entity", "user-computer-profile"], ["profiles", "captured-host"]);
        var factory = ToolsetFactory.CreateCurrentSessionToolsetFactory(
            dataAccessLayer,
            new CurrentSessionContext
            {
                AgentSessionId = AgentSessionId,
                UserComputerProfile = profile,
            });

        var provider = Assert.IsType<CurrentSessionContextProvider>(
            await factory.CreateToolsetAsync(
                new AgentSchema.CustomTool { Kind = "current-session", Name = "current-session" },
                new AgentServices()));
        var result = await InvokeToolAsync(provider, new Dictionary<string, object?>());

        Assert.Equal("captured-host", ReadFirstNameLeaf(result.GetProperty("user_computer_profile")));
    }

    [Fact]
    public async Task GetCurrentSession_HappyPath_ReturnsUserComputerAndProfile()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        await SeedAgentSessionAsync(dataAccessLayer, AgentSessionId, ["agent-sessions", "one"]);
        var profile = await SeedEntityAsync(dataAccessLayer, ["entity", "user-computer-profile"], ["profiles", "host-a"]);
        var user = await SeedEntityAsync(dataAccessLayer, ["entity", "user"], ["users", "alice"]);
        var computer = await SeedEntityAsync(dataAccessLayer, ["entity", "computer"], ["computers", "hostname", "host-a"]);

        var result = await InvokeAsync(
            dataAccessLayer,
            new CurrentSessionContext
            {
                AgentSessionId = AgentSessionId,
                UserComputerProfile = profile,
                User = user,
                Computer = computer,
            });

        Assert.Equal(JsonValueKind.Object, result.GetProperty("user").ValueKind);
        Assert.Equal(JsonValueKind.Object, result.GetProperty("computer").ValueKind);
        Assert.Equal(JsonValueKind.Object, result.GetProperty("user_computer_profile").ValueKind);
    }

    [Fact]
    public async Task GetCurrentSession_ReturnedEntities_MatchCurrentIdentity()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        await SeedAgentSessionAsync(dataAccessLayer, AgentSessionId, ["agent-sessions", "one"]);
        var profile = await SeedEntityAsync(dataAccessLayer, ["entity", "user-computer-profile"], ["profiles", "host-a"]);
        var user = await SeedEntityAsync(dataAccessLayer, ["entity", "user"], ["users", "alice"]);
        var computer = await SeedEntityAsync(dataAccessLayer, ["entity", "computer"], ["computers", "hostname", "host-a"]);

        var result = await InvokeAsync(
            dataAccessLayer,
            new CurrentSessionContext
            {
                AgentSessionId = AgentSessionId,
                UserComputerProfile = profile,
                User = user,
                Computer = computer,
            });

        Assert.Equal(profile.EntityId.Value.ToString(), ReadEntityId(result.GetProperty("user_computer_profile")));
        Assert.Equal(user.EntityId.Value.ToString(), ReadEntityId(result.GetProperty("user")));
        Assert.Equal(computer.EntityId.Value.ToString(), ReadEntityId(result.GetProperty("computer")));
    }

    [Fact]
    public async Task GetCurrentSession_ContextWithoutComputer_ResolvesComputerFromProfileReference()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        await SeedAgentSessionAsync(dataAccessLayer, AgentSessionId, ["agent-sessions", "one"]);
        await SeedEntityAsync(dataAccessLayer, ["entity", "computer"], ["computers", "hostname", "host-c"]);
        var profile = await SeedProfileWithComputerReferenceAsync(
            dataAccessLayer,
            ["profiles", "host-c"],
            ["computers", "hostname", "host-c"]);

        var result = await InvokeAsync(
            dataAccessLayer,
            new CurrentSessionContext
            {
                AgentSessionId = AgentSessionId,
                UserComputerProfile = profile,
                // Computer intentionally omitted; the tool must resolve it from the profile reference.
            });

        Assert.Equal(JsonValueKind.Object, result.GetProperty("computer").ValueKind);
        Assert.Equal("host-c", ReadFirstNameLeaf(result.GetProperty("computer")));
    }

    [Fact]
    public async Task GetCurrentSession_ToolName_IsGetCurrentSession()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var provider = new CurrentSessionContextProvider(
            dataAccessLayer,
            new CurrentSessionContext { AgentSessionId = AgentSessionId });

        var tool = Assert.Single(await GetToolsAsync(provider));

        Assert.Equal("get_current_session", tool.Name);
    }

    [Fact]
    public async Task CurrentSessionToolset_CombinedChain_ExposesGetCurrentSessionTool()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var chain = ToolsetFactory.CreateCurrentSessionToolsetFactory(
            dataAccessLayer,
            new CurrentSessionContext { AgentSessionId = AgentSessionId },
            ToolsetFactory.CreateDefaultToolsetFactory());

        var provider = Assert.IsType<CurrentSessionContextProvider>(
            await chain.CreateToolsetAsync(
                new AgentSchema.CustomTool { Kind = "current-session", Name = "current-session" },
                new AgentServices()));
        var tools = await GetToolsAsync(provider);

        Assert.Contains(tools, static tool => string.Equals(tool.Name, "get_current_session", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetCurrentSession_CopilotSessionWithResolvedHost_ReturnsPopulatedProfileAndUser()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        await SeedAgentSessionAsync(dataAccessLayer, AgentSessionId, ["agent-sessions", "one"]);
        var profile = await SeedEntityAsync(dataAccessLayer, ["entity", "user-computer-profile"], ["profiles", "host-a"]);
        var user = await SeedEntityAsync(dataAccessLayer, ["entity", "user"], ["users", "alice"]);
        var computer = await SeedEntityAsync(dataAccessLayer, ["entity", "computer"], ["computers", "hostname", "host-a"]);

        // Mirrors the running-agent / Copilot path once AgentFactory prefers the host-resolved context.
        var result = await InvokeAsync(
            dataAccessLayer,
            new CurrentSessionContext
            {
                AgentSessionId = AgentSessionId,
                UserComputerProfile = profile,
                User = user,
                Computer = computer,
            });

        Assert.Equal(JsonValueKind.Object, result.GetProperty("user_computer_profile").ValueKind);
        Assert.Equal(JsonValueKind.Object, result.GetProperty("user").ValueKind);
        Assert.Equal(JsonValueKind.Object, result.GetProperty("computer").ValueKind);
    }

    [Fact]
    public async Task GetCurrentSession_UnresolvedHost_ReturnsExplicitNullMembersNotEmptyObject()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();

        var result = await InvokeAsync(
            dataAccessLayer,
            new CurrentSessionContext { AgentSessionId = "unresolved-session" });

        // Guard against the #1236 regression: members must be present with an explicit JSON null,
        // never dropped so the object renders as "{}".
        Assert.True(result.TryGetProperty("agent_session", out var agentSession));
        Assert.Equal(JsonValueKind.Null, agentSession.ValueKind);
        Assert.True(result.TryGetProperty("user_computer_profile", out var profile));
        Assert.Equal(JsonValueKind.Null, profile.ValueKind);
        Assert.True(result.TryGetProperty("user", out var user));
        Assert.Equal(JsonValueKind.Null, user.ValueKind);
        Assert.True(result.TryGetProperty("computer", out var computer));
        Assert.Equal(JsonValueKind.Null, computer.ValueKind);
        Assert.True(result.TryGetProperty("agent_definition", out var definition));
        Assert.Equal(JsonValueKind.Null, definition.ValueKind);
    }

    private static async Task<JsonElement> InvokeAsync(
        IDataAccessLayer dataAccessLayer,
        CurrentSessionContext context,
        IDictionary<string, object?>? arguments = null)
    {
        var provider = new CurrentSessionContextProvider(dataAccessLayer, context);
        return await InvokeToolAsync(provider, arguments ?? new Dictionary<string, object?>());
    }

    private static async Task<JsonElement> InvokeToolAsync(
        CurrentSessionContextProvider provider,
        IDictionary<string, object?> arguments)
    {
        var tool = Assert.IsAssignableFrom<AIFunction>(Assert.Single(await GetToolsAsync(provider)));
        var result = await tool.InvokeAsync(new AIFunctionArguments(arguments), CancellationToken.None);
        return Assert.IsType<JsonElement>(result);
    }

    private static async Task<AITool[]> GetToolsAsync(CurrentSessionContextProvider provider)
    {
        var agent = new ChatClientAgent(new EchoChatClient(), new ChatClientAgentOptions
        {
            UseProvidedChatClientAsIs = true,
        });
        var session = await agent.CreateSessionAsync(CancellationToken.None);
        return await AIContextProviderToolReader.GetToolsAsync(provider, agent, session, CancellationToken.None);
    }

    private static async Task SeedAgentSessionAsync(
        IDataAccessLayer dataAccessLayer,
        string agentSessionId,
        string[] entityName,
        string? hostProfileEntityId = null)
    {
        var hostProfileProperty = hostProfileEntityId is null
            ? string.Empty
            : $""", "host-profile-entity-id": {JsonSerializer.Serialize(hostProfileEntityId)}""";
        using var jsonDocument = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{Guid.NewGuid():D}}",
              "entity-types": ["entity", "agent-session"],
              "names": [{{JsonSerializer.Serialize(entityName)}}],
              "agent-session-id": {{JsonSerializer.Serialize(agentSessionId)}}{{hostProfileProperty}}
            }
            """);
        await WriteEntityAsync(dataAccessLayer, jsonDocument.RootElement.Clone());
    }

    private static async Task<EntitySnapshot> SeedEntityAsync(
        IDataAccessLayer dataAccessLayer,
        string[] entityTypes,
        string[] entityName)
    {
        var entityId = new EntityId();
        using var jsonDocument = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId.Value}}",
              "entity-types": {{JsonSerializer.Serialize(entityTypes)}},
              "names": [{{JsonSerializer.Serialize(entityName)}}]
            }
            """);
        await WriteEntityAsync(dataAccessLayer, jsonDocument.RootElement.Clone());

        var getResult = await dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities = [new GetEntityRequest { EntityId = entityId }],
            },
            CancellationToken.None);
        return getResult.Batches.SelectMany(static batch => batch.Entities).Single();
    }

    private static async Task<EntitySnapshot> SeedProfileWithComputerReferenceAsync(
        IDataAccessLayer dataAccessLayer,
        string[] entityName,
        string[] computerReference)
    {
        var entityId = new EntityId();
        using var jsonDocument = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId.Value}}",
              "entity-types": ["entity", "user-computer-profile"],
              "names": [{{JsonSerializer.Serialize(entityName)}}],
              "computer-reference": {{JsonSerializer.Serialize(computerReference)}}
            }
            """);
        await WriteEntityAsync(dataAccessLayer, jsonDocument.RootElement.Clone());

        var getResult = await dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities = [new GetEntityRequest { EntityId = entityId }],
            },
            CancellationToken.None);
        return getResult.Batches.SelectMany(static batch => batch.Entities).Single();
    }

    private static async Task WriteEntityAsync(IDataAccessLayer dataAccessLayer, JsonElement entityData)
    {
        var updateResult = await dataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown { Text = "Seed entity for current-session tests." },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = new EntityId(entityData.GetProperty("entity-id").GetString()!),
                        Data = entityData,
                        EntityChangeMode = EntityChangeMode.Replace,
                    },
                ],
            },
            CancellationToken.None);
        Assert.DoesNotContain(updateResult.EntityResults, static result => result.UpdateState == UpdateState.Failed);
    }

    private static string? ReadAgentSessionId(JsonElement entity)
        => entity.GetProperty("data").GetProperty("agent-session-id").GetString();

    private static string? ReadEntityId(JsonElement entity)
        => entity.GetProperty("entityId").GetString();

    private static string? ReadFirstNameLeaf(JsonElement entity)
    {
        var names = entity.GetProperty("data").GetProperty("names");
        var firstName = names[0];
        return firstName[firstName.GetArrayLength() - 1].GetString();
    }
}
