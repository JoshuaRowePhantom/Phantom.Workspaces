using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class EntityListItemViewModelTests
{
    [AvaloniaFact]
    public void ToggleExpandCommand_UpdatesItemAndNodeExpansionState()
    {
        var node = new EntityListNodeViewModel(
            displayName: "Folder",
            entityType: "folder",
            nameComponents: ["folder"],
            sortKey: "[\"folder\"]");
        var item = new EntityListItemViewModel(
            node,
            order: 0,
            level: 0,
            itemKey: "[\"folder\"]",
            childItemKeys: ["[\"folder\",\"child\"]"],
            isExpanded: false);

        Assert.True(item.ToggleExpandCommand.CanExecute(null));
        Assert.False(item.IsExpanded);
        Assert.False(node.IsExpanded);

        item.ToggleExpandCommand.Execute(null);

        Assert.True(item.IsExpanded);
        Assert.True(node.IsExpanded);
        Assert.Equal("▴", item.ExpandArrow);
    }

    [AvaloniaFact]
    public void ToggleExpandCommand_DisabledWhenNoChildren()
    {
        var node = new EntityListNodeViewModel(
            displayName: "Leaf",
            entityType: "entity",
            nameComponents: ["leaf"],
            sortKey: "[\"leaf\"]");
        var item = new EntityListItemViewModel(
            node,
            order: 0,
            level: 0,
            itemKey: "[\"leaf\"]");

        Assert.False(item.HasChildren);
        Assert.False(item.ToggleExpandCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void JsonButton_TogglesRawJsonEditorVisibility()
    {
        var node = new EntityListNodeViewModel(
            CreateEntity(
                """
                {
                  "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                  "entity-types": ["entity", "note"],
                  "names": [["documentation","sample"]],
                  "display-name": { "default": "Sample" }
                }
                """),
            nameComponents: ["documentation", "sample"],
            sortKey: "[\"documentation\",\"sample\"]");
        var item = new EntityListItemViewModel(
            node,
            order: 0,
            level: 0,
            itemKey: "[\"documentation\",\"sample\"]");

        Assert.True(item.ShowJsonButton);
        Assert.Equal("{}", item.JsonButtonText);
        Assert.False(item.ShowRawJsonEditor);
        Assert.Contains("\"entity-types\"", item.RawJsonText, StringComparison.Ordinal);

        item.ToggleJsonViewCommand.Execute(null);
        Assert.True(item.ShowRawJsonEditor);
    }

    [AvaloniaFact]
    public async Task DeleteButton_InvokesDeleteCommand()
    {
        var deleteInvocations = 0;
        var node = new EntityListNodeViewModel(
            CreateEntity(
                """
                {
                  "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                  "entity-types": ["entity", "note"],
                  "names": [["documentation","sample"]],
                  "display-name": { "default": "Sample" }
                }
                """,
                _ =>
                {
                    deleteInvocations++;
                    return Task.CompletedTask;
                }),
            nameComponents: ["documentation", "sample"],
            sortKey: "[\"documentation\",\"sample\"]");
        var item = new EntityListItemViewModel(
            node,
            order: 0,
            level: 0,
            itemKey: "[\"documentation\",\"sample\"]");

        Assert.True(item.ShowDeleteButton);
        Assert.True(item.DeleteEntityCommand.CanExecute(null));
        item.DeleteEntityCommand.Execute(null);
        await Task.Yield();

        Assert.Equal(1, deleteInvocations);
    }

    private static SubscribedEntityViewModel CreateEntity(
        string json,
        Func<SubscribedEntityViewModel, Task>? deleteEntityAsync = null)
    {
        using var document = JsonDocument.Parse(json);
        return new SubscribedEntityViewModel(
            new EntitySnapshot
            {
                EntityId = new EntityId("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                ConcurrencyTag = new ConcurrencyTag("1"),
                ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
                Data = document.RootElement.Clone(),
                Relationships = Array.Empty<EntitySnapshot>(),
            },
            deleteEntityAsync);
    }
}
