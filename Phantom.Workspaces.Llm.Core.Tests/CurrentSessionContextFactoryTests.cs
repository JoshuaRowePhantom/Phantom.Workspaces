using Phantom.Workspaces.Data;
using Phantom.Workspaces.Testing;
using System.Text.Json;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class CurrentSessionContextFactoryTests
{
    private const string SessionId = "session-9999";
    private const string UserName = "alice";
    private const string ComputerName = "host-a";
    private const string OwningProfileEntityId = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public async Task CreateForHostAsync_ResolvesUserComputerProfileFromDataAccessLayer()
    {
        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        var dataAccessLayer = fixture.DataAccessLayer;
        var user = await SeedEntityAsync(fixture, ["entity", "user"], ["users", "username", UserName]);
        var computer = await SeedEntityAsync(fixture, ["entity", "computer"], ["computers", "hostname", ComputerName]);
        var profile = await SeedEntityAsync(
            fixture,
            ["entity", "user-computer-profile"],
            ["computer-user-profiles", "users", "username", UserName, "computers", "hostname", ComputerName]);

        var context = await CurrentSessionContextFactory.CreateForHostAsync(
            SessionId, dataAccessLayer, UserName, ComputerName, ComputerName,
            OwningProfileEntityId, 3);

        Assert.Equal(SessionId, context.AgentSessionId);
        Assert.Equal(user.EntityId, context.User!.EntityId);
        Assert.Equal(computer.EntityId, context.Computer!.EntityId);
        Assert.Equal(profile.EntityId, context.UserComputerProfile!.EntityId);
        Assert.Equal(OwningProfileEntityId, context.OwningProfileEntityId);
        Assert.Equal(3, context.OwnershipGeneration);
        Assert.Null(context.RuntimeEpoch);
    }

    [Fact]
    public async Task CreateForHostAsync_MissingProfile_LeavesProfileNullButKeepsUserAndComputer()
    {
        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        var dataAccessLayer = fixture.DataAccessLayer;
        var user = await SeedEntityAsync(fixture, ["entity", "user"], ["users", "username", UserName]);
        var computer = await SeedEntityAsync(fixture, ["entity", "computer"], ["computers", "hostname", ComputerName]);

        var context = await CurrentSessionContextFactory.CreateForHostAsync(
            SessionId, dataAccessLayer, UserName, ComputerName, ComputerName,
            OwningProfileEntityId, 0);

        Assert.Null(context.UserComputerProfile);
        Assert.Equal(user.EntityId, context.User!.EntityId);
        Assert.Equal(computer.EntityId, context.Computer!.EntityId);
    }

    [Fact]
    public async Task CreateForHostAsync_PassesThroughAgentDefinitionReference()
    {
        var dataAccessLayer = (await ValidatingEntitySeedFixture.CreateAsync()).DataAccessLayer;
        var definitionReference = new EntityName("agent-definitions", "researcher");

        var context = await CurrentSessionContextFactory.CreateForHostAsync(
            SessionId, dataAccessLayer, UserName, ComputerName, ComputerName,
            OwningProfileEntityId, 0, agentDefinitionReference: definitionReference);

        Assert.Equal(definitionReference, context.AgentDefinitionReference);
    }

    private static async Task<EntitySnapshot> SeedEntityAsync(
        ValidatingEntitySeedFixture fixture,
        string[] entityTypes,
        string[] entityName)
    {
        var entityId = new EntityId();
        var references = entityTypes.Contains("user-computer-profile", StringComparer.Ordinal)
            ? $$"""
              ,
              "computer-reference": ["computers", "hostname", "{{ComputerName}}"],
              "user-reference": ["users", "username", "{{UserName}}"]
              """
            : string.Empty;
        using var jsonDocument = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId.Value}}",
              "entity-types": {{JsonSerializer.Serialize(entityTypes)}},
              "names": [{{JsonSerializer.Serialize(entityName)}}]{{references}}
            }
            """);
        var data = jsonDocument.RootElement.Clone();

        await fixture.SeedValidEntityAsync(data);

        var getResult = await fixture.DataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities = [new GetEntityRequest { EntityId = entityId }],
            },
            CancellationToken.None);
        return getResult.Batches.SelectMany(static batch => batch.Entities).Single();
    }
}
