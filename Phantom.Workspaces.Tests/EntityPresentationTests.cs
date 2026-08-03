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

    // -------------------- #1200: names[0]-empty display name fallback --------------------

    [AvaloniaFact]
    public void ReadPrimaryName_NamesFirstArrayHasNoStringParts_ReturnsNull()
    {
        // With no display-name and no title, the fallback goes through ReadPrimaryName. If
        // names[0] is an array whose entries yield no non-whitespace strings, ReadPrimaryName
        // must return null (not "") so GetDisplayName's null-coalescing chain reaches EntityId.
        var emptyArray = CreateSnapshot(
            """
            {
              "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
              "entity-types": ["entity"],
              "names": [[]]
            }
            """);
        var nullEntry = CreateSnapshot(
            """
            {
              "entity-id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
              "entity-types": ["entity"],
              "names": [[null]]
            }
            """);
        var whitespaceEntry = CreateSnapshot(
            """
            {
              "entity-id": "cccccccc-cccc-cccc-cccc-cccccccccccc",
              "entity-types": ["entity"],
              "names": [[""]]
            }
            """);

        Assert.Equal(emptyArray.EntityId.ToString(), EntityPresentation.GetDisplayName(emptyArray));
        Assert.Equal(nullEntry.EntityId.ToString(), EntityPresentation.GetDisplayName(nullEntry));
        Assert.Equal(whitespaceEntry.EntityId.ToString(), EntityPresentation.GetDisplayName(whitespaceEntry));
    }

    [AvaloniaFact]
    public void GetDisplayName_NamesArrayEmpty_FallsBackToEntityId()
    {
        // End-to-end: an entity with no display-name, no title, and empty names[0] must return
        // its EntityId (not "") from GetDisplayName.
        var snapshot = CreateSnapshot(
            """
            {
              "entity-id": "dddddddd-dddd-dddd-dddd-dddddddddddd",
              "entity-types": ["entity"],
              "names": [[]]
            }
            """);

        var displayName = EntityPresentation.GetDisplayName(snapshot);

        Assert.False(string.IsNullOrEmpty(displayName));
        Assert.Equal(snapshot.EntityId.ToString(), displayName);
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
