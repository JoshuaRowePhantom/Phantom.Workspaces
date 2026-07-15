using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.ViewModels;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Tests;

public sealed class ShortcutManagerTests
{
    [PhantomAvaloniaFact]
    public void Shortcut_OperatorEquality_MatchesByValue()
    {
        var openShortcutByValue = new Shortcut("Open", "↗");

        Assert.True(openShortcutByValue == Shortcut.Open);
        Assert.False(openShortcutByValue != Shortcut.Open);
    }

    [PhantomAvaloniaFact]
    public async Task GetShortcutsForAsync_ReturnsOpen_WhenAHandlerApplies()
    {
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(new TestShortcutHandler(shouldApply: true, handleResult: true));

        await using var mainWindowViewModel = CreateTestMainWindowViewModel();
        var entity = CreateEntity("entity");

        var shortcuts = await GetShortcutsAsync(shortcutManager, mainWindowViewModel, entity);

        var openShortcut = Assert.Single(shortcuts);
        Assert.Equal("Open", openShortcut.Name);
        Assert.Equal("↗", openShortcut.Label);
    }

    [PhantomAvaloniaFact]
    public async Task HandleShortcutAsync_StopsAfterFirstSuccessfulHandler()
    {
        var first = new TestShortcutHandler(shouldApply: true, handleResult: true);
        var second = new TestShortcutHandler(shouldApply: true, handleResult: true);
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(first);
        shortcutManager.AddShortcutHandler(second);

        await using var mainWindowViewModel = CreateTestMainWindowViewModel();
        var entity = CreateEntity("entity");

        var handled = await shortcutManager.HandleShortcutAsync(mainWindowViewModel, Shortcut.Open, entity);

        Assert.True(handled);
        Assert.Equal(1, first.HandleCallCount);
        Assert.Equal(0, second.HandleCallCount);
    }

    [PhantomAvaloniaFact]
    public async Task ViewEntityViewModel_PopulatesShortcuts_AfterInitializeAsync()
    {
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(new TestShortcutHandler(shouldApply: true, handleResult: true));
        await using var mainWindowViewModel = CreateTestMainWindowViewModel();
        var entity = CreateEntity("entity");

        var viewEntity = new ViewEntityViewModel(
            entity,
            mainWindowViewModel,
            shortcutManager,
            indentLevel: 0);

        Assert.Empty(viewEntity.Shortcuts);

        await viewEntity.InitializeAsync();

        var shortcutViewModel = Assert.Single(viewEntity.Shortcuts);
        Assert.Equal("Open", shortcutViewModel.Shortcut.Name);
        Assert.Same(entity, shortcutViewModel.Entity);
        Assert.Same(shortcutManager, shortcutViewModel.ShortcutManager);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task ViewEntityViewModel_InitializeAsync_WithAsyncHandler_DoesNotDeadlock()
    {
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(new YieldingShortcutHandler());
        await using var mainWindowViewModel = CreateTestMainWindowViewModel();
        var entity = CreateEntity("entity");
        var viewEntity = new ViewEntityViewModel(
            entity,
            mainWindowViewModel,
            shortcutManager,
            indentLevel: 0);

        await viewEntity.InitializeAsync();

        Assert.Contains(viewEntity.Shortcuts, shortcut => shortcut.Shortcut == Shortcut.Open);
    }

    [PhantomAvaloniaFact]
    public async Task ViewEntityViewModel_ProvidesSharedEntityCardNode_WithJsonAndDeleteActions()
    {
        var shortcutManager = new ShortcutManager();
        await using var mainWindowViewModel = CreateTestMainWindowViewModel();
        var entity = CreateEntity("entity", _ => Task.CompletedTask);

        var viewEntity = new ViewEntityViewModel(
            entity,
            mainWindowViewModel,
            shortcutManager,
            indentLevel: 0);

        await viewEntity.InitializeAsync();

        var cardNode = viewEntity.EntityCardNode;
        Assert.True(cardNode.Card.ShowJsonButton);
        Assert.True(cardNode.Card.ShowDeleteButton);
    }

    [PhantomAvaloniaFact]
    public async Task ViewEntityViewModel_EntityCardNode_UsesShortcutButtonsWhenAvailable()
    {
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(
            new TestShortcutHandler(
                shouldApply: true,
                handleResult: true,
                supportedShortcutNames: [Shortcut.Open.Name, Shortcut.Delete.Name]));
        await using var mainWindowViewModel = CreateTestMainWindowViewModel();
        var entity = CreateEntity("entity", _ => Task.CompletedTask);

        var viewEntity = new ViewEntityViewModel(
            entity,
            mainWindowViewModel,
            shortcutManager,
            indentLevel: 0);
        await viewEntity.InitializeAsync();

        var cardNode = viewEntity.EntityCardNode;
        Assert.True(cardNode.Card.HasShortcuts);
        Assert.Equal(2, cardNode.Card.Shortcuts.Count);
        Assert.NotNull(cardNode.Card.ActivateShortcutCommand);
        // The JSON toggle is a dedicated card button (no longer a shortcut-bar button), so it stays available.
        Assert.True(cardNode.Card.ShowJsonButton);
        Assert.False(cardNode.Card.ShowDeleteButton);
    }

    [PhantomAvaloniaFact]
    public async Task EntityShortcutViewModel_HandleAsync_UsesShortcutManager()
    {
        var handler = new TestShortcutHandler(shouldApply: true, handleResult: true);
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(handler);
        await using var mainWindowViewModel = CreateTestMainWindowViewModel();
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

    [PhantomAvaloniaFact]
    public async Task GetShortcutsForAsync_ReturnsDelete_WhenDeleteHandlerApplies()
    {
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(new DeleteEntityShortcutHandler());
        await using var mainWindowViewModel = CreateTestMainWindowViewModel();
        var entity = CreateEntity("entity", _ => Task.CompletedTask);

        var shortcuts = await GetShortcutsAsync(shortcutManager, mainWindowViewModel, entity);

        var deleteShortcut = Assert.Single(shortcuts);
        Assert.Equal(Shortcut.Delete, deleteShortcut);
    }

    [PhantomAvaloniaFact]
    public async Task HandleShortcutAsync_TogglesJsonShortcutVisibility()
    {
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(new ToggleJsonEntityShortcutHandler());
        await using var mainWindowViewModel = CreateTestMainWindowViewModel();
        var entity = CreateEntity("entity");
        Assert.False(entity.IsRawJsonVisible);

        var handled = await shortcutManager.HandleShortcutAsync(mainWindowViewModel, Shortcut.Json, entity);

        Assert.True(handled);
        Assert.True(entity.IsRawJsonVisible);
    }

    [PhantomAvaloniaFact]
    public async Task GetShortcutsForAsync_GitWorktreeEntity_IncludesReviewShortcut()
    {
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(new ReviewWorktreeShortcutHandler());

        await using var mainWindowViewModel = CreateTestMainWindowViewModel();
        var entity = CreateEntity("git-worktree");

        var shortcuts = await GetShortcutsAsync(shortcutManager, mainWindowViewModel, entity);

        Assert.Contains(shortcuts, s => s == Shortcut.Review);
    }

    [PhantomAvaloniaFact]
    public async Task GetShortcutsForAsync_NonGitWorktreeEntity_DoesNotIncludeReviewShortcut()
    {
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(new ReviewWorktreeShortcutHandler());

        await using var mainWindowViewModel = CreateTestMainWindowViewModel();
        var entity = CreateEntity("task");

        var shortcuts = await GetShortcutsAsync(shortcutManager, mainWindowViewModel, entity);

        Assert.DoesNotContain(shortcuts, s => s == Shortcut.Review);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task GetShortcutsForAsync_WithAsyncHandler_DoesNotBlockCallingThread()
    {
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(new YieldingShortcutHandler());

        await using var mainWindowViewModel = CreateTestMainWindowViewModel();
        var entity = CreateEntity("entity");

        var shortcuts = await GetShortcutsAsync(shortcutManager, mainWindowViewModel, entity);

        Assert.Contains(Shortcut.Open, shortcuts);
    }

    [PhantomAvaloniaFact]
    public async Task PopulateShortcutsAsync_ClearsAndRepopulates_WhenCalledTwice()
    {
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(
            new TestShortcutHandler(
                shouldApply: true,
                handleResult: true,
                supportedShortcutNames: [Shortcut.Open.Name, Shortcut.Delete.Name]));
        await using var mainWindowViewModel = CreateTestMainWindowViewModel();
        var entity = CreateEntity("entity");
        var shortcuts = new ObservableCollection<EntityShortcutViewModel>
        {
            new()
            {
                Shortcut = Shortcut.Edit,
                Entity = entity,
                ShortcutManager = shortcutManager,
            },
        };

        await EntityShortcutViewModel.PopulateShortcutsAsync(shortcuts, mainWindowViewModel, entity, shortcutManager);
        await EntityShortcutViewModel.PopulateShortcutsAsync(shortcuts, mainWindowViewModel, entity, shortcutManager);

        Assert.Equal([Shortcut.Open, Shortcut.Delete], shortcuts.Select(shortcut => shortcut.Shortcut).ToArray());
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task PopulateShortcutsAsync_WithAsyncHandler_DoesNotBlockCallingThread()
    {
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(new YieldingShortcutHandler());
        await using var mainWindowViewModel = CreateTestMainWindowViewModel();
        var entity = CreateEntity("entity");
        var shortcuts = new ObservableCollection<EntityShortcutViewModel>();

        await EntityShortcutViewModel.PopulateShortcutsAsync(shortcuts, mainWindowViewModel, entity, shortcutManager);

        Assert.Contains(shortcuts, shortcut => shortcut.Shortcut == Shortcut.Open);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public async Task HandleShortcutAsync_ReviewOnGitWorktree_OpensReviewTab()
    {
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(new ReviewWorktreeShortcutHandler());

        await using var mainWindowViewModel = CreateTestMainWindowViewModel();
        await mainWindowViewModel.InitializeAsync();

        var entity = CreateEntity("git-worktree");

        var handled = await shortcutManager.HandleShortcutAsync(mainWindowViewModel, Shortcut.Review, entity);

        Assert.True(handled);
    }

    private static RepositorySource CreateInMemoryRepositorySource()
        => new UnknownRepositorySource();

    private static MainWindowViewModel CreateTestMainWindowViewModel()
    {
        return new MainWindowViewModel(
            CreateInMemoryRepositorySource(),
            new WorkspacesConfiguration { SkipStartupWorkspace = true },
            new ProfileStore(CreateTempProfileStorePath()),
            null);
    }

    private static string CreateTempProfileStorePath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            $"phantom-test-profile-{Guid.NewGuid()}");
    }

    private static SubscribedEntityViewModel CreateEntity(
        string entityType,
        Func<SubscribedEntityViewModel, Task>? deleteEntityAsync = null)
    {
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "99999999-9999-9999-9999-999999999999",
              "entity-types": ["entity", "{{entityType}}"],
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

    private static async Task<Shortcut[]> GetShortcutsAsync(
        ShortcutManager shortcutManager,
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel entity)
    {
        var shortcuts = new List<Shortcut>();
        await foreach (var shortcut in shortcutManager.GetShortcutsForAsync(mainWindowViewModel, entity))
        {
            shortcuts.Add(shortcut);
        }

        return shortcuts.ToArray();
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

        public override ValueTask<bool> ShouldApplyTo(
            MainWindowViewModel mainWindowViewModel,
            Shortcut shortcut,
            SubscribedEntityViewModel entityViewModel)
            => ValueTask.FromResult(this.shouldApply
                && this.supportedShortcutNames.Contains(shortcut.Name, StringComparer.Ordinal));

        public override Task<bool> Handle(
            MainWindowViewModel mainWindowViewModel,
            Shortcut shortcut,
            SubscribedEntityViewModel entityViewModel)
        {
            this.HandleCallCount++;
            return Task.FromResult(this.handleResult);
        }
    }

    private sealed class YieldingShortcutHandler : ShortcutHandler
    {
        public override async ValueTask<bool> ShouldApplyTo(
            MainWindowViewModel mainWindowViewModel,
            Shortcut shortcut,
            SubscribedEntityViewModel entityViewModel)
        {
            await Task.Yield();
            return shortcut == Shortcut.Open;
        }

        public override Task<bool> Handle(
            MainWindowViewModel mainWindowViewModel,
            Shortcut shortcut,
            SubscribedEntityViewModel entityViewModel)
            => Task.FromResult(true);
    }
}
