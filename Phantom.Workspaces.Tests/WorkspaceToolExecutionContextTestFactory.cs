using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Tools;

namespace Phantom.Workspaces.Tests;

internal static class WorkspaceToolExecutionContextTestFactory
{
    public static WorkspaceToolExecutionContext Create(
        IDataAccessLayer dataAccessLayer,
        string toolJson,
        EntitySnapshot? profileEntity = null)
    {
        var placeholder = CreateSnapshot(
            """
            {
              "entity-id": "00000000-0000-0000-0000-000000000000",
              "entity-types": ["entity"],
              "names": [["placeholder"]]
            }
            """);

        return new WorkspaceToolExecutionContext
        {
            DataAccessLayer = dataAccessLayer,
            CancellationToken = CancellationToken.None,
            CurrentComputerEntity = placeholder,
            CurrentUserEntity = placeholder,
            CurrentComputerUserProfileEntity = profileEntity ?? placeholder,
            ToolRelationship = placeholder,
            Participants = [placeholder],
            Tool = CreateSnapshot(toolJson),
            Schedule = placeholder,
        };
    }

    public static EntitySnapshot CreateSnapshot(string json)
    {
        using var document = JsonDocument.Parse(json);
        var entityId = TryReadEntityId(document.RootElement) ?? new EntityId(Guid.NewGuid());
        return new EntitySnapshot
        {
            EntityId = entityId,
            ModifiedTime = new Timestamp(DateTimeOffset.UnixEpoch, "0"),
            Data = document.RootElement.Clone(),
            Relationships = [],
        };
    }

    private static EntityId? TryReadEntityId(JsonElement element)
    {
        if (element.TryGetProperty("entity-id", out var entityIdElement)
            && entityIdElement.ValueKind == JsonValueKind.String
            && Guid.TryParse(entityIdElement.GetString(), out var guid))
        {
            return new EntityId(guid);
        }

        return null;
    }
}
