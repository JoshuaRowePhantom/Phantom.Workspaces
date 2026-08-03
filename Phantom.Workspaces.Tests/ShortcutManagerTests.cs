using Avalonia.Headless.XUnit;
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
    [AvaloniaFact]
    public void Shortcut_OperatorEquality_MatchesByValue()
    {
        var openShortcutByValue = new Shortcut("Open", "↗");

        Assert.True(openShortcutByValue == Shortcut.Open);
        Assert.False(openShortcutByValue != Shortcut.Open);
    }

    [AvaloniaFact]
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

    [AvaloniaFact]
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

    [AvaloniaFact]
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

        await viewEntity.InitializeAsync();

        var shortcutViewModel = Assert.Single(viewEntity.Shortcuts);
        Assert.Equal("Open", shortcutViewModel.Shortcut.Name);
        Assert.Same(entity, shortcutViewModel.Entity);
        Assert.Same(shortcutManager, shortcutViewModel.ShortcutManager);
    }

    [AvaloniaFact(Timeout = 15_000)]
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

    [AvaloniaFact]
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

    [AvaloniaFact]
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

    [AvaloniaFact]
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

    [AvaloniaFact]
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

    [AvaloniaFact]
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

    [AvaloniaFact]
    public async Task GetShortcutsForAsync_GitWorktreeEntity_IncludesReviewShortcut()
    {
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(new ReviewWorktreeShortcutHandler());

        await using var mainWindowViewModel = CreateTestMainWindowViewModel();
        var entity = CreateEntity("git-worktree");

        var shortcuts = await GetShortcutsAsync(shortcutManager, mainWindowViewModel, entity);

        Assert.Contains(shortcuts, s => s == Shortcut.Review);
    }

    [AvaloniaFact]
    public async Task GetShortcutsForAsync_NonGitWorktreeEntity_DoesNotIncludeReviewShortcut()
    {
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(new ReviewWorktreeShortcutHandler());

        await using var mainWindowViewModel = CreateTestMainWindowViewModel();
        var entity = CreateEntity("task");

        var shortcuts = await GetShortcutsAsync(shortcutManager, mainWindowViewModel, entity);

        Assert.DoesNotContain(shortcuts, s => s == Shortcut.Review);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task GetShortcutsForAsync_WithAsyncHandler_DoesNotBlockCallingThread()
    {
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(new YieldingShortcutHandler());

        await using var mainWindowViewModel = CreateTestMainWindowViewModel();
        var entity = CreateEntity("entity");

        var shortcuts = await GetShortcutsAsync(shortcutManager, mainWindowViewModel, entity);

        Assert.Contains(Shortcut.Open, shortcuts);
    }

    [AvaloniaFact]
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

        var first = await EntityShortcutViewModel.PopulateShortcutsAsync(mainWindowViewModel, entity, shortcutManager);
        Assert.NotEmpty(first);

        var second = await EntityShortcutViewModel.PopulateShortcutsAsync(mainWindowViewModel, entity, shortcutManager);

        Assert.Equal([Shortcut.Open, Shortcut.Delete], second.Select(shortcut => shortcut.Shortcut).ToArray());
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task PopulateShortcutsAsync_WithAsyncHandler_DoesNotBlockCallingThread()
    {
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(new YieldingShortcutHandler());
        await using var mainWindowViewModel = CreateTestMainWindowViewModel();
        var entity = CreateEntity("entity");

        var shortcuts = await EntityShortcutViewModel.PopulateShortcutsAsync(mainWindowViewModel, entity, shortcutManager);

        Assert.Contains(shortcuts, shortcut => shortcut.Shortcut == Shortcut.Open);
    }

    [AvaloniaFact(Timeout = 15_000)]
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

    [AvaloniaFact(Timeout = 15_000)]
    public async Task GetShortcutsForAsync_LocalWorktreeWithNoTunnel_OmitsVsCodeWeb()
    {
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(new OpenInVsCodeWebShortcutHandler());

        await using var mainWindowViewModel = CreateTestMainWindowViewModel();
        await mainWindowViewModel.InitializeAsync();

        // Entity has a path but no tunnel is seeded — handler must NOT include VsCodeWeb
        using var document = JsonDocument.Parse("""
            {
                "entity-id": "11111111-2222-4333-4444-555555555555",
                "entity-types": ["entity", "git-worktree", "filesystem-path"],
                "names": [["tests", "worktrees", "no-tunnel-check"]],
                "display-name": { "default": "No Tunnel Worktree" },
                "path": "/test/no-tunnel"
            }
            """);
        var entity = new SubscribedEntityViewModel(
            new EntitySnapshot
            {
                EntityId = new EntityId("11111111-2222-4333-4444-555555555555"),
                ConcurrencyTag = new ConcurrencyTag("1"),
                ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
                Data = document.RootElement.Clone(),
                Relationships = Array.Empty<EntitySnapshot>(),
            });

        var shortcuts = await GetShortcutsAsync(shortcutManager, mainWindowViewModel, entity);

        Assert.DoesNotContain(shortcuts, s => s == Shortcut.VsCodeWeb);
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

    [AvaloniaFact]
    public async Task PopulateShortcutsAsync_WhenCalledConcurrently_EachInvocationReturnsCompleteDedupedList()
    {
        // Fix #1144 — PopulateShortcutsAsync builds a fresh list and returns it. Concurrent
        // invocations must each return a full deduped list without corrupting one another.
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(new YieldingShortcutHandler());
        await using var mainWindowViewModel = CreateTestMainWindowViewModel();
        var entity = CreateEntity("entity");

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => EntityShortcutViewModel.PopulateShortcutsAsync(mainWindowViewModel, entity, shortcutManager))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        foreach (var result in results)
        {
            Assert.Single(result);
            Assert.Equal(Shortcut.Open, result[0].Shortcut);
        }
    }

    [AvaloniaFact]
    public async Task GetShortcutsForAsync_EntityMatchingMultipleTypeNames_ReturnsDedupedShortcutSet()
    {
        // Fix #1144 — the built list must never contain the same shortcut twice, even when many
        // handlers apply. First-wins in GetShortcutsForAsync guarantees this.
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(
            new TestShortcutHandler(shouldApply: true, handleResult: true,
                supportedShortcutNames: [Shortcut.Open.Name]));
        shortcutManager.AddShortcutHandler(
            new TestShortcutHandler(shouldApply: true, handleResult: true,
                supportedShortcutNames: [Shortcut.Open.Name]));
        await using var mainWindowViewModel = CreateTestMainWindowViewModel();
        var entity = CreateEntity("git-worktree");

        var shortcuts = await GetShortcutsAsync(shortcutManager, mainWindowViewModel, entity);

        Assert.Equal(shortcuts.Length, shortcuts.Distinct().Count());
        Assert.Single(shortcuts, s => s == Shortcut.Open);
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
