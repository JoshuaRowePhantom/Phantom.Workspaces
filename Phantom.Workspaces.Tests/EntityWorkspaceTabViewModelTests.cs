using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phantom.Workspaces.Controls;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Gui.Shared.Controls;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class EntityWorkspaceTabViewModelTests : IAsyncDisposable
{
    private readonly MainWindowViewModel mainWindowViewModel;

    public EntityWorkspaceTabViewModelTests()
    {
        this.mainWindowViewModel = new MainWindowViewModel(new UnknownRepositorySource());
    }

    public async ValueTask DisposeAsync()
    {
        await this.mainWindowViewModel.DisposeAsync();
    }

    [AvaloniaFact]
    public void EntityWorkspaceTabViewModel_SingleEntityCard_ShowsShortcuts()
    {
        this.mainWindowViewModel.ShortcutManager.AddShortcutHandler(new TestShortcutHandler(Shortcut.Open.Name));
        var tab = new EntityWorkspaceTabViewModel(mainWindowViewModel: this.mainWindowViewModel)
        {
            Id = "test-tab",
            Title = "Test",
            Entity = CreateEntity("entity"),
        };

        var cardNode = tab.EntityCardNode;

        Assert.NotNull(cardNode);
        Assert.True(cardNode!.Card.HasShortcuts);
        Assert.Contains(cardNode.Card.Shortcuts, shortcut => shortcut.Shortcut == Shortcut.Open);
        Assert.NotNull(cardNode.Card.ActivateShortcutCommand);
    }

    [AvaloniaFact]
    public void EntityWorkspaceTabViewModel_ExternalAndNote_OpenAndReopenShowsBoth()
    {
        var entity = new SubscribedEntityViewModel(ExternalEntityCardViewModelTests.MixedExternalNoteTestData.CreateSnapshot());
        EntityWorkspaceTabViewModel Open() => new()
        {
            Id = "mixed",
            Title = "Mixed",
            Entity = entity,
        };

        var first = Open().EntityCardNode!.Card;
        Assert.Equal("external-note", first.CardViewName);
        Assert.True(first.ShowFieldEditors);
        Assert.Equal("https://example.com/first", Assert.Single(first.ExternalCard!.Urls).Url);
        Assert.Empty(first.FieldEditors);

        var reopened = Open().EntityCardNode!.Card;
        Assert.Equal("external-note", reopened.CardViewName);
        Assert.NotNull(reopened.ExternalCard);
        Assert.True(reopened.ShowFieldEditors);

        entity.UpdateSnapshot(ExternalEntityCardViewModelTests.MixedExternalNoteTestData.CreateSnapshot(types: "\"entity\", \"external\""));
        Assert.Equal("external", first.CardViewName);
        Assert.False(first.ShowFieldEditors);
        Assert.Equal("external", Open().EntityCardNode!.Card.CardViewName);

        entity.UpdateSnapshot(ExternalEntityCardViewModelTests.MixedExternalNoteTestData.CreateSnapshot(types: "\"entity\", \"note\""));
        Assert.Equal("raw", first.CardViewName);
        Assert.Null(first.ExternalCard);
        Assert.Equal("raw", Open().EntityCardNode!.Card.CardViewName);
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task EntityWorkspaceTabViewModel_ExternalAndNote_OpenAndReopenRendersLinksAndMarkdown()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(), TestContext.Current.CancellationToken);
        var catalog = await EntityTypeViewCatalog.CreateAsync(broker);
        var entity = new SubscribedEntityViewModel(
            ExternalEntityCardViewModelTests.MixedExternalNoteTestData.CreateSnapshot());

        EntityWorkspaceTabViewModel Open() => new(broker, catalog)
        {
            Id = "mixed",
            Title = "Mixed",
            Entity = entity,
        };

        static void AssertRendered(Window window, EntityCardViewModel card, string url, string markdown)
        {
            Assert.Same(card, Assert.Single(window.GetVisualDescendants()
                .OfType<EntityCardControl>()).DataContext);
            Assert.Equal("content", Assert.Single(card.FieldEditors).FieldName);
            var link = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                button => button.Classes.Contains("workspace-url-link"));
            Assert.Equal(url, link.Content);
            Assert.NotNull(link.Command);
            Assert.Equal(markdown, Assert.Single(
                window.GetVisualDescendants().OfType<WorkspaceMarkdownView>()).Markdown);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(),
                block => block.Text?.Contains("must never appear", StringComparison.Ordinal) == true);
        }

        var firstTab = Open();
        var firstCard = firstTab.EntityCardNode!.Card;
        Assert.Empty(firstCard.FieldEditors);
        var firstWindow = new Window { Content = firstTab, Width = 900, Height = 600 };
        try
        {
            firstWindow.Show();
            Dispatcher.UIThread.RunJobs();
            await firstCard.FieldEditorsBuildTask;
            Dispatcher.UIThread.RunJobs();
            AssertRendered(firstWindow, firstCard, "https://example.com/first", "# First note");

            entity.UpdateSnapshot(ExternalEntityCardViewModelTests.MixedExternalNoteTestData.CreateSnapshot(
                url: "https://example.com/second", body: "# Second note"));
            await firstCard.FieldEditorsBuildTask;
            Dispatcher.UIThread.RunJobs();
            AssertRendered(firstWindow, firstCard, "https://example.com/second", "# Second note");
        }
        finally
        {
            firstWindow.Close();
        }

        var reopenedTab = Open();
        var reopenedCard = reopenedTab.EntityCardNode!.Card;
        Assert.Empty(reopenedCard.FieldEditors);
        var reopenedWindow = new Window { Content = reopenedTab, Width = 900, Height = 600 };
        try
        {
            reopenedWindow.Show();
            Dispatcher.UIThread.RunJobs();
            await reopenedCard.FieldEditorsBuildTask;
            Dispatcher.UIThread.RunJobs();
            AssertRendered(reopenedWindow, reopenedCard, "https://example.com/second", "# Second note");
        }
        finally
        {
            reopenedWindow.Close();
        }
    }

    private static SubscribedEntityViewModel CreateEntity(string entityType)
    {
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "66666666-6666-6666-6666-666666666666",
              "entity-types": ["entity", "{{entityType}}"],
              "names": [["tests", "{{entityType}}"]],
              "display-name": { "default": "Test {{entityType}}" }
            }
            """);
        return new SubscribedEntityViewModel(
            new EntitySnapshot
            {
                EntityId = new EntityId("66666666-6666-6666-6666-666666666666"),
                ConcurrencyTag = new ConcurrencyTag("1"),
                ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
                Data = document.RootElement.Clone(),
                Relationships = Array.Empty<EntitySnapshot>(),
            });
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
}
