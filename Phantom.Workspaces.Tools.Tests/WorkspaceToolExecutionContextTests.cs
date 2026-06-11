using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;

namespace Phantom.Workspaces.Tools.Tests;

public sealed class WorkspaceToolExecutionContextTests
{
    [Fact]
    public async Task Context_CarriesCurrentComputerUserAndProfileEntities()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();

        var currentComputerEntity = await UpsertEntityAsync(
            dataAccessLayer,
            new EntityId("11111111-1111-1111-1111-111111111111"),
            """
            {
              "entity-id": "11111111-1111-1111-1111-111111111111",
              "entity-types": ["computer"],
              "names": [["computers", "hostname", "test-computer"]]
            }
            """,
            concurrencyTag: null);

        var currentUserEntity = await UpsertEntityAsync(
            dataAccessLayer,
            new EntityId("22222222-2222-2222-2222-222222222222"),
            """
            {
              "entity-id": "22222222-2222-2222-2222-222222222222",
              "entity-types": ["user"],
              "names": [["users", "username", "test-user"]]
            }
            """,
            concurrencyTag: null);

        var currentComputerUserProfileEntity = await UpsertEntityAsync(
            dataAccessLayer,
            new EntityId("33333333-3333-3333-3333-333333333333"),
            """
            {
              "entity-id": "33333333-3333-3333-3333-333333333333",
              "entity-types": ["user-computer-profile"],
              "names": [["computer-user-profiles", "computers", "hostname", "test-computer", "users", "username", "test-user"]],
              "computer-reference": ["computers", "hostname", "test-computer"],
              "user-reference": ["users", "username", "test-user"],
              "home-directory": "C:\\Users\\test-user"
            }
            """,
            concurrencyTag: null);

        var context = new WorkspaceToolExecutionContext
        {
            DataAccessLayer = dataAccessLayer,
            CancellationToken = CancellationToken.None,
            CurrentComputerEntity = currentComputerEntity,
            CurrentUserEntity = currentUserEntity,
            CurrentComputerUserProfileEntity = currentComputerUserProfileEntity,
            ToolRelationship = currentComputerUserProfileEntity,
            Participants = [currentComputerUserProfileEntity],
            Tool = currentUserEntity,
            Schedule = currentComputerEntity,
        };

        Assert.Equal(currentComputerEntity.EntityId, context.CurrentComputerEntity.EntityId);
        Assert.Equal(currentUserEntity.EntityId, context.CurrentUserEntity.EntityId);
        Assert.Equal(currentComputerUserProfileEntity.EntityId, context.CurrentComputerUserProfileEntity.EntityId);
    }

    private static async Task<EntitySnapshot> UpsertEntityAsync(
        IDataAccessLayer dataAccessLayer,
        EntityId entityId,
        string json,
        ConcurrencyTag? concurrencyTag)
    {
        using var document = JsonDocument.Parse(json);
        var updateResult = await dataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Workspace tool execution context test upsert.",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = entityId,
                        ConcurrencyTag = concurrencyTag,
                        EntityChangeMode = EntityChangeMode.Replace,
                        Data = document.RootElement.Clone(),
                    },
                ],
            });

        var entityResult = Assert.Single(updateResult.EntityResults, entityResult => entityResult.RequestedEntityId == entityId);
        Assert.Empty(entityResult.Errors);
        return Assert.IsType<EntitySnapshot>(entityResult.CurrentEntity);
    }
}
