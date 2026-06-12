using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class ShortcutManagerTests
{
    [AvaloniaFact]
    public void Shortcut_OperatorEquality_MatchesByValue()
    {
        var openShortcutByValue = new Shortcut("Open", "Open");

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
        Assert.Equal("Open", openShortcut.Label);
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

    private static RepositorySource CreateInMemoryRepositorySource()
        => new(RepositorySourceType.Unknown, "(none)");

    private static SubscribedEntityViewModel CreateEntity(
        string entityType)
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
            });
    }

    private sealed class TestShortcutHandler : ShortcutHandler
    {
        private readonly bool shouldApply;
        private readonly bool handleResult;

        public TestShortcutHandler(
            bool shouldApply,
            bool handleResult)
        {
            this.shouldApply = shouldApply;
            this.handleResult = handleResult;
        }

        public int HandleCallCount { get; private set; }

        public override bool ShouldApplyTo(
            MainWindowViewModel mainWindowViewModel,
            Shortcut shortcut,
            SubscribedEntityViewModel entityViewModel)
            => this.shouldApply;

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
