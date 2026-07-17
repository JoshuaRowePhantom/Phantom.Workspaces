using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class EntityCardViewModelTests : IAsyncDisposable
{
    private readonly MainWindowViewModel mainWindowViewModel;

    public EntityCardViewModelTests()
    {
        this.mainWindowViewModel = new MainWindowViewModel(new UnknownRepositorySource());
    }

    public async ValueTask DisposeAsync()
    {
        await this.mainWindowViewModel.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task EntityCardViewModel_WhenShortcutManagerSet_ResolvesShortcuts()
    {
        var card = new EntityCardViewModel(CreateEntity("entity"));
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(new TestShortcutHandler(Shortcut.Open.Name));

        card.SetShortcutContext(this.mainWindowViewModel, shortcutManager);
        await card.ResolveShortcutsAsync();

        Assert.True(card.HasShortcuts);
        Assert.Contains(card.Shortcuts, shortcut => shortcut.Shortcut == Shortcut.Open);
    }

    [AvaloniaFact]
    public async Task EntityCardViewModel_WhenSubscribedEntityDataChanges_ReResolvesShortcuts()
    {
        var entity = CreateEntity("note");
        var card = new EntityCardViewModel(entity);
        var shortcutManager = new ShortcutManager();
        // Handler only applies the Delete shortcut to entities whose type is "task".
        shortcutManager.AddShortcutHandler(new EntityTypeShortcutHandler("task", Shortcut.Delete.Name));

        card.SetShortcutContext(this.mainWindowViewModel, shortcutManager);
        await card.ResolveShortcutsAsync();
        Assert.False(card.HasShortcuts);

        entity.UpdateSnapshot(CreateSnapshot("task"));

        // Changing the entity's snapshot re-runs shortcut resolution automatically.
        Assert.True(card.HasShortcuts);
        Assert.Contains(card.Shortcuts, shortcut => shortcut.Shortcut == Shortcut.Delete);
    }

    [AvaloniaFact]
    public void EntityCardViewModel_WhenNoShortcutManager_HasShortcutsIsFalse()
    {
        var card = new EntityCardViewModel(CreateEntity("entity"));

        Assert.False(card.HasShortcuts);
        Assert.Empty(card.Shortcuts);
    }

    [AvaloniaFact]
    public void EntityCardViewModel_WhenShortcutManagerSet_WiresActivateShortcutCommand()
    {
        var card = new EntityCardViewModel(CreateEntity("entity"));
        var shortcutManager = new ShortcutManager();

        card.SetShortcutContext(this.mainWindowViewModel, shortcutManager);

        Assert.NotNull(card.ActivateShortcutCommand);
        Assert.Same(this.mainWindowViewModel.ActivateShortcutCommand, card.ActivateShortcutCommand);
    }

    private static SubscribedEntityViewModel CreateEntity(string entityType)
    {
        return new SubscribedEntityViewModel(
            CreateSnapshot(entityType),
            deleteEntityAsync: _ => Task.CompletedTask);
    }

    private static EntitySnapshot CreateSnapshot(string entityType)
    {
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "77777777-7777-7777-7777-777777777777",
              "entity-types": ["entity", "{{entityType}}"],
              "names": [["tests", "{{entityType}}"]],
              "display-name": { "default": "Test {{entityType}}" }
            }
            """);
        return new EntitySnapshot
        {
            EntityId = new EntityId("77777777-7777-7777-7777-777777777777"),
            ConcurrencyTag = new ConcurrencyTag("1"),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, Guid.NewGuid().ToString()),
            Data = document.RootElement.Clone(),
            Relationships = Array.Empty<EntitySnapshot>(),
        };
    }

    private sealed class TestShortcutHandler : ShortcutHandler
    {
        private readonly string shortcutName;

        public TestShortcutHandler(string shortcutName)
        {
            this.shortcutName = shortcutName;
        }

        public override ValueTask<bool> ShouldApplyTo(
            MainWindowViewModel mainWindowViewModel,
            Shortcut shortcut,
            SubscribedEntityViewModel entityViewModel)
            => ValueTask.FromResult(string.Equals(shortcut.Name, this.shortcutName, StringComparison.Ordinal));

        public override Task<bool> Handle(
            MainWindowViewModel mainWindowViewModel,
            Shortcut shortcut,
            SubscribedEntityViewModel entityViewModel)
            => Task.FromResult(true);
    }

    private sealed class EntityTypeShortcutHandler : ShortcutHandler
    {
        private readonly string entityType;
        private readonly string shortcutName;

        public EntityTypeShortcutHandler(string entityType, string shortcutName)
        {
            this.entityType = entityType;
            this.shortcutName = shortcutName;
        }

        public override ValueTask<bool> ShouldApplyTo(
            MainWindowViewModel mainWindowViewModel,
            Shortcut shortcut,
            SubscribedEntityViewModel entityViewModel)
            => ValueTask.FromResult(
                string.Equals(shortcut.Name, this.shortcutName, StringComparison.Ordinal)
                && entityViewModel.IsEntityType(this.entityType));

        public override Task<bool> Handle(
            MainWindowViewModel mainWindowViewModel,
            Shortcut shortcut,
            SubscribedEntityViewModel entityViewModel)
            => Task.FromResult(true);
    }
}
