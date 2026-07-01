using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class EntityPresentationTests
{
    [PhantomAvaloniaFact]
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

    [PhantomAvaloniaFact]
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

    [PhantomAvaloniaFact]
    public void GetDisplayItems_ReturnsInlineMarkdownBody_ForNoteContent()
    {
        var snapshot = CreateSnapshot(
            """
            {
              "entity-id": "56565656-5656-5656-5656-565656565656",
              "entity-types": ["entity", "note"],
              "names": [["documentation", "agent-manifests"]],
              "title": { "default": "Agent Manifests" },
              "content": {
                "default": {
                  "mime-type": "text/markdown",
                  "content": { "text": "# Agent Manifests\n\nThis is the body." }
                }
              }
            }
            """);

        var items = EntityPresentation.GetDisplayItems(snapshot);

        var item = Assert.Single(items);
        Assert.Contains("# Agent Manifests", item.Text, StringComparison.Ordinal);
        Assert.Contains("This is the body.", item.Text, StringComparison.Ordinal);
    }

    [PhantomAvaloniaFact]
    public void GetDisplayItems_ReturnsEmpty_WhenNoteHasNoContent()
    {
        var snapshot = CreateSnapshot(
            """
            {
              "entity-id": "67676767-6767-6767-6767-676767676767",
              "entity-types": ["entity", "note"],
              "names": [["documentation", "empty"]],
              "title": { "default": "Empty" }
            }
            """);

        Assert.Empty(EntityPresentation.GetDisplayItems(snapshot));
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
