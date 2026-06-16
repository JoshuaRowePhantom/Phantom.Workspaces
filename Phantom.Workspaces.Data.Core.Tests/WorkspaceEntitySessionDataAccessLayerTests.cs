using System.Text.Json;
using Phantom.Workspaces.Data.Offline;

namespace Phantom.Workspaces.Data.Tests;

public sealed class WorkspaceEntitySessionDataAccessLayerTests
{
    [Fact]
    public async Task CreateEntityNames_UsesDefaultNamePrefixesAndSessionMetaVariables()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var workspaceEntitySession = await SeedWorkspaceEntitySessionAsync(dataAccessLayer);
        await UpsertEntityAsync(
            dataAccessLayer,
            new EntityId("80ecfcbf-03ed-461d-9353-f06c7f9faab8"),
            """
            {
              "entity-id": "80ecfcbf-03ed-461d-9353-f06c7f9faab8",
              "entity-types": ["entity-type", "note"],
              "names": [["entity-types", "agent-session"]],
              "default-name-prefixes": [["${USER}", "agent-sessions"]]
            }
            """);

        var names = await WorkspaceEntityNameFactory.CreateEntityNames(
            dataAccessLayer,
            workspaceEntitySession,
            new EntityTypeName("agent-session"),
            "session-001");

        Assert.Equal(2, names.Length);
        Assert.Contains(new EntityName("users", "username", "test-user", "agent-sessions", "session-001"), names);
        Assert.Contains(new EntityName("users", "id", "test-user-id", "agent-sessions", "session-001"), names);
    }

    [Fact]
    public async Task GetAsync_RewritesMetaVariableEntityNameUsingWorkspaceEntitySession()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var workspaceEntitySession = await SeedWorkspaceEntitySessionAsync(dataAccessLayer);
        var targetEntityId = new EntityId("9e50e8d6-df48-4f7f-a33b-5d4915fd5960");
        await UpsertEntityAsync(
            dataAccessLayer,
            targetEntityId,
            """
            {
              "entity-id": "9e50e8d6-df48-4f7f-a33b-5d4915fd5960",
              "entity-types": ["agent-session"],
              "names": [["users", "id", "test-user-id", "agent-sessions", "session-001"]],
              "agent-session-id": "session-001"
            }
            """);

        var workspaceEntitySessionDataAccessLayer = new WorkspaceEntitySessionDataAccessLayer(dataAccessLayer, workspaceEntitySession);
        var getResult = await workspaceEntitySessionDataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityName = new EntityName("${USER}", "agent-sessions", "session-001"),
                    },
                ],
            });

        var entity = Assert.Single(Assert.Single(getResult.Batches).Entities);
        Assert.Equal(targetEntityId, entity.EntityId);
    }

    [Fact]
    public async Task CreateEntityNames_WhenNoDefaultNamePrefixes_ReturnsSimpleName()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var workspaceEntitySession = await SeedWorkspaceEntitySessionAsync(dataAccessLayer);
        await UpsertEntityAsync(
            dataAccessLayer,
            new EntityId("8a5f64c9-4ece-43e2-a4c8-a04f66f31872"),
            """
            {
              "entity-id": "8a5f64c9-4ece-43e2-a4c8-a04f66f31872",
              "entity-types": ["entity-type", "note"],
              "names": [["entity-types", "note"]],
              "default-name-prefixes": []
            }
            """);

        var names = await WorkspaceEntityNameFactory.CreateEntityNames(
            dataAccessLayer,
            workspaceEntitySession,
            new EntityTypeName("note"),
            "test-note");

        Assert.Equal([new EntityName("test-note")], names);
    }

    [Fact]
    public async Task QueryAsync_BindsUserMetaVariable_InFieldClauseValue()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var workspaceEntitySession = await SeedWorkspaceEntitySessionAsync(dataAccessLayer);

        // A task assigned to the session user, and one assigned to another user.
        var assignedTask = new EntityId("aaaaaaaa-1111-1111-1111-111111111111");
        var otherTask = new EntityId("bbbbbbbb-2222-2222-2222-222222222222");
        var otherUser = new EntityId("cccccccc-3333-3333-3333-333333333333");
        await UpsertEntityAsync(dataAccessLayer, assignedTask, """{ "entity-id": "aaaaaaaa-1111-1111-1111-111111111111", "entity-types": ["task"], "names": [["tasks","a"]] }""");
        await UpsertEntityAsync(dataAccessLayer, otherTask, """{ "entity-id": "bbbbbbbb-2222-2222-2222-222222222222", "entity-types": ["task"], "names": [["tasks","b"]] }""");
        await UpsertEntityAsync(dataAccessLayer, new EntityId("dddddddd-4444-4444-4444-444444444444"),
            """{ "entity-id": "dddddddd-4444-4444-4444-444444444444", "entity-types": ["assigned-to","relationship"], "names": [["relationships","r1"]], "participants": { "target": "aaaaaaaa-1111-1111-1111-111111111111", "user": "11111111-1111-1111-1111-111111111111" } }""");
        await UpsertEntityAsync(dataAccessLayer, new EntityId("eeeeeeee-5555-5555-5555-555555555555"),
            $$"""{ "entity-id": "eeeeeeee-5555-5555-5555-555555555555", "entity-types": ["assigned-to","relationship"], "names": [["relationships","r2"]], "participants": { "target": "bbbbbbbb-2222-2222-2222-222222222222", "user": "{{otherUser.Value}}" } }""");

        var sessionDataAccessLayer = new WorkspaceEntitySessionDataAccessLayer(dataAccessLayer, workspaceEntitySession);

        var result = await sessionDataAccessLayer.QueryAsync(new QueryRequest
        {
            Clauses =
            [
                new TopLevelQueryClause
                {
                    ClauseIdentifier = new QueryClauseIdentifier("assigned"),
                    Clause = new EntityParticipationQueryClause
                    {
                        RelationshipTypeNames = new RelationshipTypeNameSet(["assigned-to"]),
                        ParticipationRoleNames = new RoleNameSet(["target"]),
                        MustHave = new EntityParticipationRequirement
                        {
                            ParticipationRoleNames = new RoleNameSet(["user"]),
                            Clause = new EntityFieldQueryClause
                            {
                                FieldPath = new FieldPath("entity-id"),
                                ComparisonOperator = FieldComparisonOperator.Equals,
                                Value = JsonSerializer.SerializeToElement(WorkspaceEntityMetaVariables.User),
                            },
                        },
                    },
                },
            ],
        });

        var ids = result.Batches.SelectMany(batch => batch.Entities).Select(entity => entity.EntityId).ToHashSet();
        Assert.Contains(assignedTask, ids);
        Assert.DoesNotContain(otherTask, ids);
    }

    private static async Task<WorkspaceEntitySession> SeedWorkspaceEntitySessionAsync(
        IDataAccessLayer dataAccessLayer)
    {
        var userEntityId = new EntityId("11111111-1111-1111-1111-111111111111");
        var computerEntityId = new EntityId("22222222-2222-2222-2222-222222222222");
        var userComputerProfileEntityId = new EntityId("33333333-3333-3333-3333-333333333333");
        await UpsertEntityAsync(
            dataAccessLayer,
            userEntityId,
            """
            {
              "entity-id": "11111111-1111-1111-1111-111111111111",
              "entity-types": ["user"],
              "names": [
                ["users", "username", "test-user"],
                ["users", "id", "test-user-id"]
              ]
            }
            """);
        await UpsertEntityAsync(
            dataAccessLayer,
            computerEntityId,
            """
            {
              "entity-id": "22222222-2222-2222-2222-222222222222",
              "entity-types": ["computer"],
              "names": [["computers", "hostname", "test-computer"]]
            }
            """);
        await UpsertEntityAsync(
            dataAccessLayer,
            userComputerProfileEntityId,
            """
            {
              "entity-id": "33333333-3333-3333-3333-333333333333",
              "entity-types": ["user-computer-profile"],
              "names": [["computer-user-profiles", "users", "username", "test-user", "computers", "hostname", "test-computer"]]
            }
            """);

        return new WorkspaceEntitySession
        {
            UserEntityId = userEntityId,
            ComputerEntityId = computerEntityId,
            UserComputerProfileEntityId = userComputerProfileEntityId,
        };
    }

    private static async Task UpsertEntityAsync(
        IDataAccessLayer dataAccessLayer,
        EntityId entityId,
        string json)
    {
        using var document = JsonDocument.Parse(json);
        var updateResult = await dataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Workspace entity session DAL test upsert.",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = entityId,
                        EntityChangeMode = EntityChangeMode.Replace,
                        Data = document.RootElement.Clone(),
                    },
                ],
            });
        var entityResult = Assert.Single(updateResult.EntityResults, entityResult => entityResult.RequestedEntityId == entityId);
        Assert.Empty(entityResult.Errors);
        Assert.NotEqual(UpdateState.Failed, entityResult.UpdateState);
    }
}
