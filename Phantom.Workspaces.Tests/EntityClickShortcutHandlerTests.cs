using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Tests;

public sealed class EntityClickShortcutHandlerTests
{
    [PhantomAvaloniaFact]
    public async Task Handle_ConfiguredEntityType_InvokesOpenViaManager()
    {
        var openRecorder = new RecordingOpenHandler();
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(openRecorder);
        var clickHandler = new EntityClickShortcutHandler(["workspace"], shortcutManager);

        await using var mainWindowViewModel = new MainWindowViewModel(new UnknownRepositorySource());
        var workspace = CreateEntity("workspace");

        var handled = await clickHandler.Handle(mainWindowViewModel, Shortcut.Open, workspace);

        Assert.True(handled);
        Assert.Equal(1, openRecorder.HandleCallCount);
        Assert.Equal(Shortcut.Open, openRecorder.LastShortcut);
        Assert.Same(workspace, openRecorder.LastEntity);
    }

    [PhantomAvaloniaFact]
    public async Task Handle_NonConfiguredEntityType_DoesNothing()
    {
        var openRecorder = new RecordingOpenHandler();
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(openRecorder);
        var clickHandler = new EntityClickShortcutHandler(["workspace"], shortcutManager);

        await using var mainWindowViewModel = new MainWindowViewModel(new UnknownRepositorySource());
        var note = CreateEntity("note");

        var handled = await clickHandler.Handle(mainWindowViewModel, Shortcut.Open, note);

        Assert.False(handled);
        Assert.Equal(0, openRecorder.HandleCallCount);
    }

    [PhantomAvaloniaFact]
    public async Task ShouldApplyTo_MatchesOnlyConfiguredTypes()
    {
        var shortcutManager = new ShortcutManager();
        var clickHandler = new EntityClickShortcutHandler(["workspace"], shortcutManager);
        await using var mainWindowViewModel = new MainWindowViewModel(new UnknownRepositorySource());

        Assert.True(await clickHandler.ShouldApplyTo(mainWindowViewModel, Shortcut.Open, CreateEntity("workspace")));
        Assert.False(await clickHandler.ShouldApplyTo(mainWindowViewModel, Shortcut.Open, CreateEntity("note")));
    }

    [PhantomAvaloniaFact]
    public async Task UnregisteredClickHandler_ContributesNoShortcutButton()
    {
        // The production wiring keeps the click handler out of the manager, so it never affects the
        // buttons returned by GetShortcutsForAsync.
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(new OpenEntityShortcutHandler());
        await using var mainWindowViewModel = new MainWindowViewModel(new UnknownRepositorySource());
        var workspace = CreateEntity("workspace");

        var before = await GetShortcutsAsync(shortcutManager, mainWindowViewModel, workspace);

        // Constructing the click handler (without registering it) must not change the buttons.
        _ = new EntityClickShortcutHandler(["workspace"], shortcutManager);
        var after = await GetShortcutsAsync(shortcutManager, mainWindowViewModel, workspace);

        Assert.Equal(before, after);
        Assert.Contains(Shortcut.Open, after);
    }

    private static SubscribedEntityViewModel CreateEntity(string entityType)
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
            deleteEntityAsync: null);
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

    private sealed class RecordingOpenHandler : ShortcutHandler
    {
        public int HandleCallCount { get; private set; }

        public Shortcut? LastShortcut { get; private set; }

        public SubscribedEntityViewModel? LastEntity { get; private set; }

        public override ValueTask<bool> ShouldApplyTo(
            MainWindowViewModel mainWindowViewModel,
            Shortcut shortcut,
            SubscribedEntityViewModel entityViewModel)
            => ValueTask.FromResult(shortcut == Shortcut.Open);

        public override Task<bool> Handle(
            MainWindowViewModel mainWindowViewModel,
            Shortcut shortcut,
            SubscribedEntityViewModel entityViewModel)
        {
            this.HandleCallCount++;
            this.LastShortcut = shortcut;
            this.LastEntity = entityViewModel;
            return Task.FromResult(true);
        }
    }
}

