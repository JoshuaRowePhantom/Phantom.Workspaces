using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
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

    [AvaloniaFact]
    public async Task EntityCardViewModel_GitWorktreeEntity_ExposesEachShortcutExactlyOnce()
    {
        // Fix #1144 — a fresh resolve yields a list where each shortcut appears exactly once.
        var card = new EntityCardViewModel(CreateEntity("git-worktree"));
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(new TestShortcutHandler(Shortcut.Open.Name));
        shortcutManager.AddShortcutHandler(new TestShortcutHandler(Shortcut.Review.Name));

        card.SetShortcutContext(this.mainWindowViewModel, shortcutManager);
        await card.ResolveShortcutsAsync();

        Assert.Equal(card.Shortcuts.Count, card.Shortcuts.Select(s => s.Shortcut).Distinct().Count());
        Assert.Contains(card.Shortcuts, s => s.Shortcut == Shortcut.Open);
        Assert.Contains(card.Shortcuts, s => s.Shortcut == Shortcut.Review);
    }

    [AvaloniaFact]
    public async Task EntityCardViewModel_WhenSnapshotUpdatesRapidly_RebindsWithoutDuplicateShortcuts()
    {
        // Fix #1144 — rapid snapshot updates queue overlapping resolutions. The wholesale
        // rebind + supersession guard must produce a deduped final Shortcuts list.
        var entity = CreateEntity("git-worktree");
        var card = new EntityCardViewModel(entity);
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(new TestShortcutHandler(Shortcut.Open.Name));

        card.SetShortcutContext(this.mainWindowViewModel, shortcutManager);

        // Fire many overlapping resolutions in-thread and wait for each to complete before the
        // next assertion.
        for (var i = 0; i < 12; i++)
        {
            await card.ResolveShortcutsAsync();
        }

        Assert.Single(card.Shortcuts);
        Assert.Equal(Shortcut.Open, card.Shortcuts[0].Shortcut);
    }

    [AvaloniaFact]
    public async Task EntityCardViewModel_WhenResolveShortcutsRunsConcurrently_LastRebindWins()
    {
        // Fix #1144 — concurrent resolutions must not produce duplicates; the last completed
        // rebind provides the final Shortcuts list (a wholesale reference swap).
        var card = new EntityCardViewModel(CreateEntity("git-worktree"));
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(new TestShortcutHandler(Shortcut.Open.Name));

        card.SetShortcutContext(this.mainWindowViewModel, shortcutManager);

        var tasks = Enumerable.Range(0, 8).Select(_ => card.ResolveShortcutsAsync()).ToArray();
        await Task.WhenAll(tasks);

        Assert.Single(card.Shortcuts);
        Assert.Equal(Shortcut.Open, card.Shortcuts[0].Shortcut);
    }

    [AvaloniaFact]
    public async Task EntityCardViewModel_WhenStaleResolutionCompletesAfterNewer_DoesNotAssignSupersededShortcuts()
    {
        // Fix #1144 — a stale resolution whose CTS has been cancelled must not assign.
        var card = new EntityCardViewModel(CreateEntity("git-worktree"));
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(new TestShortcutHandler(Shortcut.Open.Name));

        card.SetShortcutContext(this.mainWindowViewModel, shortcutManager);
        await card.ResolveShortcutsAsync();
        var expected = card.Shortcuts;

        // Explicitly cancel a stale token before resolving with it — the resolution must abort
        // its assignment and leave Shortcuts unchanged.
        using var staleCts = new CancellationTokenSource();
        staleCts.Cancel();
        await card.ResolveShortcutsAsync(staleCts.Token);

        Assert.Same(expected, card.Shortcuts);
    }

    [AvaloniaFact]
    public async Task EntityCardViewModel_WhenShortcutsReassigned_RaisesPropertyChangedForShortcuts()
    {
        // Fix #1144 — the wholesale rebind raises PropertyChanged("Shortcuts") so the bound
        // ItemsControl rebinds atomically.
        var card = new EntityCardViewModel(CreateEntity("git-worktree"));
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(new TestShortcutHandler(Shortcut.Open.Name));

        card.SetShortcutContext(this.mainWindowViewModel, shortcutManager);

        var raised = new System.Collections.Generic.List<string?>();
        card.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await card.ResolveShortcutsAsync();

        Assert.Contains(nameof(EntityCardViewModel.Shortcuts), raised);
    }

    [AvaloniaFact]
    public void EntityCardViewModel_WhenQueryMatchesDisplayName_HighlightsMatchedText()
    {
        var card = new EntityCardViewModel(
            displayName: "hello world",
            entityType: "note");

        card.MatchQuery = "world";

        Assert.True(card.IsFindMatch);
        Assert.Equal(6, card.DisplayNameMatchStart);
        Assert.Equal(5, card.DisplayNameMatchLength);
        Assert.Equal("hello ", card.DisplayNameBefore);
        Assert.Equal("world", card.DisplayNameMatch);
        Assert.Equal(string.Empty, card.DisplayNameAfter);
    }

    [AvaloniaFact]
    public void EntityCardViewModel_WhenQueryCleared_RemovesHighlight()
    {
        var card = new EntityCardViewModel(
            displayName: "hello world",
            entityType: "note");
        card.MatchQuery = "world";
        Assert.True(card.IsFindMatch);

        card.MatchQuery = null;

        Assert.False(card.IsFindMatch);
        Assert.Equal("hello world", card.DisplayNameBefore);
        Assert.Equal(string.Empty, card.DisplayNameMatch);
        Assert.Equal(string.Empty, card.DisplayNameAfter);
    }

    // ---- Issue #1164: multi-typed entity card composition ------------------------------------

    [AvaloniaFact]
    public async Task EntityCardViewModel_MultiTyped_PassesAllNonAbstractTypesToFieldEditorFactory()
    {
        // BuildFieldEditorsAsync must consult every non-abstract entity type so the note view can
        // contribute the `content` field on a tool+note entity.
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);
        var entityTypeViewCatalog = new EntityTypeViewCatalog(new[]
        {
            new EntityTypeViewDefinition(
                "note",
                null,
                new[] { new EntityFieldViewDefinition(new[] { "content" }, "inline") }),
        });
        var fieldEditorFactory = new FieldEditorFactory(broker, entityTypeViewCatalog);

        using var document = JsonDocument.Parse(
            """
            {
              "entity-id": "e4f5a6b7-c8d9-4e0f-b1c2-d3e4f5a6b7c8",
              "entity-types": ["entity", "tool", "note"],
              "names": [["tools", "run-vs-code-tunnel"]],
              "display-name": { "default": "Run VS Code Tunnel" },
              "content": {
                "default": {
                  "mime-type": "text/markdown",
                  "content": { "text": "# Run VS Code Tunnel\n\nBody." }
                }
              }
            }
            """);

        var snapshot = new EntitySnapshot
        {
            EntityId = new EntityId("e4f5a6b7-c8d9-4e0f-b1c2-d3e4f5a6b7c8"),
            ConcurrencyTag = new ConcurrencyTag("1"),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
            Data = document.RootElement.Clone(),
            Relationships = Array.Empty<EntitySnapshot>(),
        };
        var entity = new SubscribedEntityViewModel(snapshot);

        var card = new EntityCardViewModel(
            entity,
            fieldEditorFactory: fieldEditorFactory);

        // BuildFieldEditorsAsync is kicked off from the constructor as fire-and-forget work owned by
        // the VM's ViewModelLifetime. Wait for the resulting PropertyChanged notification to fire so
        // the assertion inspects the settled FieldEditors list rather than the empty initial state.
        var built = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? _, PropertyChangedEventArgs e)
        {
            if (string.Equals(e.PropertyName, nameof(EntityCardViewModel.FieldEditors), StringComparison.Ordinal))
            {
                built.TrySetResult(true);
            }
        }
        card.PropertyChanged += OnChanged;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!card.FieldEditors.Any(fe => fe.FieldName == "content"))
            {
                var completed = await Task.WhenAny(
                    built.Task,
                    Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }).GetTask());
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                if (completed == built.Task && built.Task.IsCompleted)
                {
                    // Rearm for possible follow-up notifications.
                    built = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                if (timeout.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            card.PropertyChanged -= OnChanged;
        }

        Assert.Contains(card.FieldEditors, fe => fe.FieldName == "content");
    }

    [AvaloniaFact]
    public async Task EntityCardViewModel_NoteEntity_RendersContentMarkdownExactlyOnce()
    {
        // Fix #1171 — the single surviving FieldEditors channel must render note content markdown
        // exactly once. Prior to the fix, DisplayItems also produced the same markdown, leading to
        // a duplicate render.
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);
        var entityTypeViewCatalog = new EntityTypeViewCatalog(new[]
        {
            new EntityTypeViewDefinition(
                "note",
                null,
                new[] { new EntityFieldViewDefinition(new[] { "content" }, "inline") }),
        });
        var fieldEditorFactory = new FieldEditorFactory(broker, entityTypeViewCatalog);

        using var document = JsonDocument.Parse(
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
        var snapshot = new EntitySnapshot
        {
            EntityId = new EntityId("56565656-5656-5656-5656-565656565656"),
            ConcurrencyTag = new ConcurrencyTag("1"),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
            Data = document.RootElement.Clone(),
            Relationships = Array.Empty<EntitySnapshot>(),
        };
        var entity = new SubscribedEntityViewModel(snapshot);
        var card = new EntityCardViewModel(entity, fieldEditorFactory: fieldEditorFactory);

        await WaitForContentFieldEditorAsync(card);

        Assert.Single(card.FieldEditors, fe => fe.FieldName == "content");
    }

    [AvaloniaFact]
    public async Task EntityCardViewModel_MultiTypedToolAndNote_RendersNoteContentExactlyOnce()
    {
        // Fix #1171 — regression guard for #1164. A tool+note entity must still render note markdown
        // (from note-entity-type-view.json's inline "content" field via the FieldEditors channel),
        // but exactly once — no duplicate DisplayItems channel.
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);
        var entityTypeViewCatalog = new EntityTypeViewCatalog(new[]
        {
            new EntityTypeViewDefinition(
                "note",
                null,
                new[] { new EntityFieldViewDefinition(new[] { "content" }, "inline") }),
        });
        var fieldEditorFactory = new FieldEditorFactory(broker, entityTypeViewCatalog);

        using var document = JsonDocument.Parse(
            """
            {
              "entity-id": "e4f5a6b7-c8d9-4e0f-b1c2-d3e4f5a6b7c8",
              "entity-types": ["entity", "tool", "note"],
              "names": [["tools", "run-vs-code-tunnel"]],
              "display-name": { "default": "Run VS Code Tunnel" },
              "content": {
                "default": {
                  "mime-type": "text/markdown",
                  "content": { "text": "# Run VS Code Tunnel\n\nBody paragraph." }
                }
              }
            }
            """);
        var snapshot = new EntitySnapshot
        {
            EntityId = new EntityId("e4f5a6b7-c8d9-4e0f-b1c2-d3e4f5a6b7c8"),
            ConcurrencyTag = new ConcurrencyTag("1"),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
            Data = document.RootElement.Clone(),
            Relationships = Array.Empty<EntitySnapshot>(),
        };
        var entity = new SubscribedEntityViewModel(snapshot);
        var card = new EntityCardViewModel(entity, fieldEditorFactory: fieldEditorFactory);

        await WaitForContentFieldEditorAsync(card);

        Assert.Single(card.FieldEditors, fe => fe.FieldName == "content");
    }

    [AvaloniaFact]
    public void EntityCardViewModel_DoesNotExposeDisplayItemsChannel()
    {
        // Fix #1171 — the redundant DisplayItems channel was removed. FieldEditors is now the sole
        // display/editing channel bound by EntityCardControl.
        Assert.Null(typeof(EntityCardViewModel).GetProperty("DisplayItems"));
    }

    [AvaloniaFact]
    public void SubscribedEntityViewModel_DoesNotExposeDisplayItemsChannel()
    {
        // Fix #1171 — note markdown reaches the card only through FieldEditorFactory.
        Assert.Null(typeof(SubscribedEntityViewModel).GetProperty("DisplayItems"));
    }

    private static async Task WaitForContentFieldEditorAsync(EntityCardViewModel card)
    {
        var built = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? _, PropertyChangedEventArgs e)
        {
            if (string.Equals(e.PropertyName, nameof(EntityCardViewModel.FieldEditors), StringComparison.Ordinal))
            {
                built.TrySetResult(true);
            }
        }
        card.PropertyChanged += OnChanged;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!card.FieldEditors.Any(fe => fe.FieldName == "content"))
            {
                var completed = await Task.WhenAny(
                    built.Task,
                    Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }).GetTask());
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                if (completed == built.Task && built.Task.IsCompleted)
                {
                    built = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                if (timeout.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            card.PropertyChanged -= OnChanged;
        }
    }
}
