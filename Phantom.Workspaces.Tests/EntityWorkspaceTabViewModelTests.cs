using System;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Phantom.Workspaces.Data;
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
