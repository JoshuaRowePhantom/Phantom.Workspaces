using Avalonia.Headless.XUnit;
using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class EntityPresentationTests
{
    [AvaloniaFact]
    public void IsEntityType_ReturnsTrue_WhenMatchingTypeIsPresent()
    {
        var snapshot = CreateSnapshot(
            """
            {
              "entity-id": "12121212-1212-1212-1212-121212121212",
              "entity-types": ["entity", "view", "agent-definition"],
              "names": [["tests", "entity"]],
              "display-name": { "default": "Entity" }
            }
            """);

        Assert.True(EntityPresentation.IsEntityType(snapshot, "agent-definition"));
        Assert.False(EntityPresentation.IsEntityType(snapshot, "agent-session"));
    }

    [AvaloniaFact]
    public void SubscribedEntityViewModel_IsEntityType_UsesTypeMembershipCheck()
    {
        var snapshot = CreateSnapshot(
            """
            {
              "entity-id": "34343434-3434-3434-3434-343434343434",
              "entity-types": ["entity", "view", "workspace"],
              "names": [["tests", "workspace"]],
              "display-name": { "default": "Workspace Entity" }
            }
            """);
        var entity = new SubscribedEntityViewModel(snapshot);

        Assert.True(entity.IsEntityType("workspace"));
        Assert.True(entity.IsEntityType("view"));
        Assert.False(entity.IsEntityType("agent-session"));
    }

    [AvaloniaFact]
    public void EntityPresentation_MultipleNonAbstractTypes_ReturnsAllOrdered()
    {
        // Issue #1164: composition depends on iterating every non-abstract type in declaration
        // order, so a card can contribute per-type presentations for tool THEN note.
        var snapshot = CreateSnapshot(
            """
            {
              "entity-id": "e4f5a6b7-c8d9-4e0f-b1c2-d3e4f5a6b7c8",
              "entity-types": ["entity", "tool", "note"],
              "names": [["tools", "run-vs-code-tunnel"]],
              "display-name": { "default": "Run VS Code Tunnel" }
            }
            """);

        Assert.Equal(new[] { "tool", "note" }, EntityPresentation.GetNonAbstractEntityTypeNames(snapshot));
    }

    private static EntitySnapshot CreateSnapshot(
        string json)
    {
        using var document = JsonDocument.Parse(json);
        return new EntitySnapshot
        {
            EntityId = new EntityId(document.RootElement.GetProperty("entity-id").GetString()!),
            ConcurrencyTag = new ConcurrencyTag("1"),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
            Data = document.RootElement.Clone(),
            Relationships = Array.Empty<EntitySnapshot>(),
        };
    }
}
