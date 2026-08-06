using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using System.Text.Json;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class CurrentSessionContextFactoryTests
{
    private const string SessionId = "session-9999";
    private const string UserName = "alice";
    private const string ComputerName = "host-a";

    [Fact]
    public async Task CreateForHostAsync_ResolvesUserComputerProfileFromDataAccessLayer()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var user = await SeedEntityAsync(dataAccessLayer, ["entity", "user"], ["users", "username", UserName]);
        var computer = await SeedEntityAsync(dataAccessLayer, ["entity", "computer"], ["computers", "hostname", ComputerName]);
        var profile = await SeedEntityAsync(
            dataAccessLayer,
            ["entity", "user-computer-profile"],
            ["computer-user-profiles", "users", "username", UserName, "computers", "hostname", ComputerName]);

        var context = await CurrentSessionContextFactory.CreateForHostAsync(
            SessionId, dataAccessLayer, UserName, ComputerName, ComputerName);

        Assert.Equal(SessionId, context.AgentSessionId);
        Assert.Equal(user.EntityId, context.User!.EntityId);
        Assert.Equal(computer.EntityId, context.Computer!.EntityId);
        Assert.Equal(profile.EntityId, context.UserComputerProfile!.EntityId);
    }

    [Fact]
    public async Task CreateForHostAsync_MissingProfile_LeavesProfileNullButKeepsUserAndComputer()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var user = await SeedEntityAsync(dataAccessLayer, ["entity", "user"], ["users", "username", UserName]);
        var computer = await SeedEntityAsync(dataAccessLayer, ["entity", "computer"], ["computers", "hostname", ComputerName]);

        var context = await CurrentSessionContextFactory.CreateForHostAsync(
            SessionId, dataAccessLayer, UserName, ComputerName, ComputerName);

        Assert.Null(context.UserComputerProfile);
        Assert.Equal(user.EntityId, context.User!.EntityId);
        Assert.Equal(computer.EntityId, context.Computer!.EntityId);
    }

    [Fact]
    public async Task CreateForHostAsync_PassesThroughAgentDefinitionReference()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var definitionReference = new EntityName("agent-definitions", "researcher");

        var context = await CurrentSessionContextFactory.CreateForHostAsync(
            SessionId, dataAccessLayer, UserName, ComputerName, ComputerName, definitionReference);

        Assert.Equal(definitionReference, context.AgentDefinitionReference);
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
        var data = jsonDocument.RootElement.Clone();

        var updateResult = await dataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown { Text = "Seed entity for current-session factory tests." },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = entityId,
                        Data = data,
                        EntityChangeMode = EntityChangeMode.Replace,
                    },
                ],
            },
            CancellationToken.None);
        Assert.DoesNotContain(updateResult.EntityResults, static result => result.UpdateState == UpdateState.Failed);

        var getResult = await dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities = [new GetEntityRequest { EntityId = entityId }],
            },
            CancellationToken.None);
        return getResult.Batches.SelectMany(static batch => batch.Entities).Single();
    }
}
