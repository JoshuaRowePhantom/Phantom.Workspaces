using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class ShortcutManagerTests
{
    [AvaloniaFact]
    public void Shortcut_OperatorEquality_MatchesByValue()
    {
        var openShortcutByValue = new Shortcut("Open", "↗");

        Assert.True(openShortcutByValue == Shortcut.Open);
        Assert.False(openShortcutByValue != Shortcut.Open);
    }

    [AvaloniaFact]
    public void GetShortcutsFor_ReturnsOpen_WhenAHandlerApplies()
    {
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(new TestShortcutHandler(shouldApply: true, handleResult: true));

        var mainWindowViewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        var entity = CreateEntity("entity");

        var shortcuts = shortcutManager.GetShortcutsFor(mainWindowViewModel, entity).ToArray();

        var openShortcut = Assert.Single(shortcuts);
        Assert.Equal("Open", openShortcut.Name);
        Assert.Equal("↗", openShortcut.Label);
    }

    [AvaloniaFact]
    public async Task HandleShortcutAsync_StopsAfterFirstSuccessfulHandler()
    {
        var first = new TestShortcutHandler(shouldApply: true, handleResult: true);
        var second = new TestShortcutHandler(shouldApply: true, handleResult: true);
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(first);
        shortcutManager.AddShortcutHandler(second);

        var mainWindowViewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        var entity = CreateEntity("entity");

        var handled = await shortcutManager.HandleShortcutAsync(mainWindowViewModel, Shortcut.Open, entity);

        Assert.True(handled);
        Assert.Equal(1, first.HandleCallCount);
        Assert.Equal(0, second.HandleCallCount);
    }

    [AvaloniaFact]
    public void ViewEntityViewModel_PopulatesShortcuts_FromShortcutManager()
    {
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(new TestShortcutHandler(shouldApply: true, handleResult: true));
        var mainWindowViewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        var entity = CreateEntity("entity");

        var viewEntity = new ViewEntityViewModel(
            entity,
            mainWindowViewModel,
            shortcutManager,
            indentLevel: 0);

        var shortcutViewModel = Assert.Single(viewEntity.Shortcuts);
        Assert.Equal("Open", shortcutViewModel.Shortcut.Name);
        Assert.Same(entity, shortcutViewModel.Entity);
        Assert.Same(shortcutManager, shortcutViewModel.ShortcutManager);
    }

    [AvaloniaFact]
    public void ViewEntityViewModel_ProvidesSharedEntityCardNode_WithJsonAndDeleteActions()
    {
        var shortcutManager = new ShortcutManager();
        var mainWindowViewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        var entity = CreateEntity("entity", _ => Task.CompletedTask);

        var viewEntity = new ViewEntityViewModel(
            entity,
            mainWindowViewModel,
            shortcutManager,
            indentLevel: 0);

        var cardNode = viewEntity.EntityCardNode;
        Assert.True(cardNode.ShowJsonButton);
        Assert.True(cardNode.ShowDeleteButton);
    }

    [AvaloniaFact]
    public async Task EntityShortcutViewModel_HandleAsync_UsesShortcutManager()
    {
        var handler = new TestShortcutHandler(shouldApply: true, handleResult: true);
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(handler);
        var mainWindowViewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        var entity = CreateEntity("entity");
        var shortcutViewModel = new EntityShortcutViewModel
        {
            Shortcut = Shortcut.Open,
            Entity = entity,
            ShortcutManager = shortcutManager,
        };

        var handled = await shortcutViewModel.HandleAsync(mainWindowViewModel);

        Assert.True(handled);
        Assert.Equal(1, handler.HandleCallCount);
    }

    [AvaloniaFact]
    public void GetShortcutsFor_ReturnsDelete_WhenDeleteHandlerApplies()
    {
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(new DeleteEntityShortcutHandler());
        var mainWindowViewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        var entity = CreateEntity("entity", _ => Task.CompletedTask);

        var shortcuts = shortcutManager.GetShortcutsFor(mainWindowViewModel, entity).ToArray();

        var deleteShortcut = Assert.Single(shortcuts);
        Assert.Equal(Shortcut.Delete, deleteShortcut);
    }

    [AvaloniaFact]
    public async Task HandleShortcutAsync_TogglesJsonShortcutVisibility()
    {
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(new ToggleJsonEntityShortcutHandler());
        var mainWindowViewModel = new MainWindowViewModel(CreateInMemoryRepositorySource());
        var entity = CreateEntity("entity");
        Assert.False(entity.IsRawJsonVisible);

        var handled = await shortcutManager.HandleShortcutAsync(mainWindowViewModel, Shortcut.Json, entity);

        Assert.True(handled);
        Assert.True(entity.IsRawJsonVisible);
    }

    private static RepositorySource CreateInMemoryRepositorySource()
        => new(RepositorySourceType.Unknown, "(none)");

    private static SubscribedEntityViewModel CreateEntity(
        string entityType,
        Func<SubscribedEntityViewModel, Task>? deleteEntityAsync = null)
    {
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "99999999-9999-9999-9999-999999999999",
              "entity-types": ["{{entityType}}"],
              "names": [["tests", "{{entityType}}"]],
              "display-name": { "default": "Test {{entityType}}" }
            }
            """);
        return new SubscribedEntityViewModel(
            new EntitySnapshot
            {
                EntityId = new EntityId("99999999-9999-9999-9999-999999999999"),
                ConcurrencyTag = new ConcurrencyTag("1"),
                ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
                Data = document.RootElement.Clone(),
                Relationships = Array.Empty<EntitySnapshot>(),
            },
            deleteEntityAsync);
    }

    private sealed class TestShortcutHandler : ShortcutHandler
    {
        private readonly bool shouldApply;
        private readonly bool handleResult;
        private readonly IReadOnlyCollection<string> supportedShortcutNames;

        public TestShortcutHandler(
            bool shouldApply,
            bool handleResult,
            IReadOnlyCollection<string>? supportedShortcutNames = null)
        {
            this.shouldApply = shouldApply;
            this.handleResult = handleResult;
            this.supportedShortcutNames = supportedShortcutNames ?? [Shortcut.Open.Name];
        }

        public int HandleCallCount { get; private set; }

        public override bool ShouldApplyTo(
            MainWindowViewModel mainWindowViewModel,
            Shortcut shortcut,
            SubscribedEntityViewModel entityViewModel)
            => this.shouldApply
                && this.supportedShortcutNames.Contains(shortcut.Name, StringComparer.Ordinal);

        public override Task<bool> Handle(
            MainWindowViewModel mainWindowViewModel,
            Shortcut shortcut,
            SubscribedEntityViewModel entityViewModel)
        {
            this.HandleCallCount++;
            return Task.FromResult(this.handleResult);
        }
    }
}
