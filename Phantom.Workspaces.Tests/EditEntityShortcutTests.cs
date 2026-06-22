using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class EditEntityShortcutTests
{
    private static SubscribedEntityViewModel CreateEntity(
        bool editable)
    {
        using var document = JsonDocument.Parse(
            """
            {
              "entity-id": "abcabcab-1111-2222-3333-444455556666",
              "entity-types": ["note"],
              "names": [["tests", "editable"]],
              "display-name": { "default": "Editable" }
            }
            """);
        var snapshot = new EntitySnapshot
        {
            EntityId = new EntityId("abcabcab-1111-2222-3333-444455556666"),
            ConcurrencyTag = new ConcurrencyTag("1"),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
            Data = document.RootElement.Clone(),
            Relationships = Array.Empty<EntitySnapshot>(),
        };
        return new SubscribedEntityViewModel(
            snapshot,
            saveEntityAsync: editable ? (_, _) => Task.CompletedTask : null);
    }

    [AvaloniaFact]
    public void ShouldApplyTo_TrueForEditableEntity_FalseOtherwise()
    {
        var handler = new EditEntityShortcutHandler();
        var mainWindow = new MainWindowViewModel(new UnknownRepositorySource());

        Assert.True(handler.ShouldApplyTo(mainWindow, Shortcut.Edit, CreateEntity(editable: true)));
        Assert.False(handler.ShouldApplyTo(mainWindow, Shortcut.Edit, CreateEntity(editable: false)));
        Assert.False(handler.ShouldApplyTo(mainWindow, Shortcut.Open, CreateEntity(editable: true)));
    }

    [AvaloniaFact]
    public void GetShortcutsFor_IncludesEdit_ForEditableEntity()
    {
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(new EditEntityShortcutHandler());
        var mainWindow = new MainWindowViewModel(new UnknownRepositorySource());

        var editableShortcuts = shortcutManager.GetShortcutsFor(mainWindow, CreateEntity(editable: true)).ToArray();
        var readOnlyShortcuts = shortcutManager.GetShortcutsFor(mainWindow, CreateEntity(editable: false)).ToArray();

        Assert.Contains(Shortcut.Edit, editableShortcuts);
        Assert.DoesNotContain(Shortcut.Edit, readOnlyShortcuts);
    }

    [AvaloniaFact]
    public async Task Handle_PutsCardNodeIntoEditMode()
    {
        var entity = CreateEntity(editable: true);
        var cardNode = new EntityListNodeViewModel(entity, ["tests", "editable"], "sort");
        var handler = new EditEntityShortcutHandler(_ => cardNode);
        var mainWindow = new MainWindowViewModel(new UnknownRepositorySource());

        Assert.False(cardNode.IsEditMode);
        var handled = await handler.Handle(mainWindow, Shortcut.Edit, entity);

        Assert.True(handled);
        Assert.True(cardNode.IsEditMode);
    }

    [AvaloniaFact]
    public void EnterEditMode_DisablesShortcutsAndBadges()
    {
        var entity = CreateEntity(editable: true);
        var cardNode = new EntityListNodeViewModel(entity, ["tests", "editable"], "sort");

        Assert.True(cardNode.AreShortcutsEnabled);
        Assert.True(cardNode.AreBadgesEnabled);

        cardNode.EnterEditMode();

        Assert.True(cardNode.IsEditMode);
        Assert.False(cardNode.AreShortcutsEnabled);
        Assert.False(cardNode.AreBadgesEnabled);
    }
}
