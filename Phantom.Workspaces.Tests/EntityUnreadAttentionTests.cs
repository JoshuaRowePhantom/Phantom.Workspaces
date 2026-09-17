using System;
using System.ComponentModel;
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
using Phantom.Workspaces.Services.Notifications;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class EntityUnreadAttentionTests
{
    [Fact]
    public void SubscribedEntityViewModel_HasUnreadAttention_DefaultIsFalse()
    {
        var entity = CreateEntity();

        Assert.False(entity.HasUnreadAttention);
    }

    [Fact]
    public void SubscribedEntityViewModel_SetHasUnreadAttention_RaisesPropertyChanged()
    {
        var entity = CreateEntity();
        string? changedProperty = null;
        entity.PropertyChanged += (_, args) => changedProperty = args.PropertyName;

        entity.HasUnreadAttention = true;

        Assert.Equal(nameof(SubscribedEntityViewModel.HasUnreadAttention), changedProperty);
    }

    [Fact]
    public void EntityCardViewModel_EntityHasUnreadAttentionTrue_HasUnreadAttentionIsTrue()
    {
        var entity = CreateEntity();
        entity.HasUnreadAttention = true;

        var card = new EntityCardViewModel(entity);

        Assert.True(card.HasUnreadAttention);
    }

    [Fact]
    public void EntityCardViewModel_EntityHasUnreadAttentionChanges_RaisesPropertyChangedForHasUnreadAttention()
    {
        var entity = CreateEntity();
        var card = new EntityCardViewModel(entity);
        PropertyChangedEventArgs? changed = null;
        card.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(EntityCardViewModel.HasUnreadAttention))
            {
                changed = args;
            }
        };

        entity.HasUnreadAttention = true;

        Assert.NotNull(changed);
        Assert.True(card.HasUnreadAttention);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowViewModel_TabNotificationAdded_SetsEntityHasUnreadAttentionTrue()
    {
        await using var viewModel = await CreateInitializedViewModelAsync();
        var entity = CreateEntity();
        var tab = await OpenInactiveEntityTabAsync(viewModel, entity, "attention-added");

        NotifyUnread(viewModel, tab.Id);

        Assert.True(entity.HasUnreadAttention);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowViewModel_TabNotificationMarkedRead_ClearsEntityHasUnreadAttention()
    {
        await using var viewModel = await CreateInitializedViewModelAsync();
        var entity = CreateEntity();
        var tab = await OpenInactiveEntityTabAsync(viewModel, entity, "attention-read");
        NotifyUnread(viewModel, tab.Id);

        viewModel.NotificationService.MarkRead(tab.Id);

        Assert.False(entity.HasUnreadAttention);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowViewModel_TabClosedWithUnreadNotification_ClearsEntityHasUnreadAttention()
    {
        await using var viewModel = await CreateInitializedViewModelAsync();
        var entity = CreateEntity();
        var tab = await OpenInactiveEntityTabAsync(viewModel, entity, "attention-closed");
        NotifyUnread(viewModel, tab.Id);
        Assert.True(entity.HasUnreadAttention);

        viewModel.CloseTab(tab);

        Assert.False(entity.HasUnreadAttention);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowViewModel_TwoTabsSameEntity_OneUnreadOneRead_EntityHasUnreadAttentionIsTrue()
    {
        await using var viewModel = await CreateInitializedViewModelAsync();
        var entity = CreateEntity();
        var first = CreateTab("attention-shared-first", entity);
        var second = CreateTab("attention-shared-second", entity);
        await viewModel.OpenTabAsync(first);
        await viewModel.OpenTabAsync(second);

        NotifyUnread(viewModel, first.Id);

        Assert.True(entity.HasUnreadAttention);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowViewModel_TwoTabsSameEntity_BothMarkedRead_EntityHasUnreadAttentionIsFalse()
    {
        await using var viewModel = await CreateInitializedViewModelAsync();
        var entity = CreateEntity();
        var first = CreateTab("attention-both-read-first", entity);
        var second = CreateTab("attention-both-read-second", entity);
        await viewModel.OpenTabAsync(first);
        await viewModel.OpenTabAsync(second);
        NotifyUnread(viewModel, first.Id);

        viewModel.NotificationService.MarkRead(first.Id);

        Assert.False(entity.HasUnreadAttention);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowViewModel_PaneTabUnread_SetsWorkspaceEntityHasUnreadAttentionTrue()
    {
        await using var viewModel = await CreateInitializedViewModelAsync();
        var pane = viewModel.SelectedWorkspacePane;
        var tab = await OpenInactiveEntityTabAsync(viewModel, CreateEntity(), "attention-pane");

        NotifyUnread(viewModel, tab.Id);

        Assert.True(pane.Entity.HasUnreadAttention);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowViewModel_AllPaneTabsRead_ClearsWorkspaceEntityHasUnreadAttention()
    {
        await using var viewModel = await CreateInitializedViewModelAsync();
        var pane = viewModel.SelectedWorkspacePane;
        var tab = await OpenInactiveEntityTabAsync(viewModel, CreateEntity(), "attention-pane-read");
        NotifyUnread(viewModel, tab.Id);

        viewModel.NotificationService.MarkRead(tab.Id);

        Assert.False(pane.Entity.HasUnreadAttention);
    }

    [Fact]
    public void MainWindowViewModel_MultipleCardInstancesSameEntity_BothReflectHasUnreadAttention()
    {
        var entity = CreateEntity();
        var firstCard = new EntityCardViewModel(entity);
        var secondCard = new EntityCardViewModel(entity);

        entity.HasUnreadAttention = true;

        Assert.True(firstCard.HasUnreadAttention);
        Assert.True(secondCard.HasUnreadAttention);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowViewModel_UnrelatedEntityNotificationUnaffected_EntityHasUnreadAttentionRemainsFalse()
    {
        await using var viewModel = await CreateInitializedViewModelAsync();
        var notifiedEntity = CreateEntity();
        var unrelatedEntity = CreateEntity();
        var tab = await OpenInactiveEntityTabAsync(viewModel, notifiedEntity, "attention-unrelated");

        NotifyUnread(viewModel, tab.Id);

        Assert.True(notifiedEntity.HasUnreadAttention);
        Assert.False(unrelatedEntity.HasUnreadAttention);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardControl_HasUnreadAttentionTrue_ExclamationIndicatorRendersLeftOfTitle()
    {
        var entity = CreateEntity();
        entity.HasUnreadAttention = true;
        var control = new EntityCardControl { DataContext = new EntityCardViewModel(entity) };
        var window = new Window { Content = control, Width = 400, Height = 200 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var titleRow = window.GetVisualDescendants()
                .OfType<Grid>()
                .Single(grid => grid.Classes.Contains("workspace-entity-title-row"));
            var indicator = Assert.IsType<ProgressBar>(titleRow.Children[0]);
            var title = Assert.IsType<SafeSelectableTextBlock>(titleRow.Children[1]);
            var glyph = indicator.GetVisualDescendants()
                .OfType<TextBlock>()
                .Single(text => text.Name == "Glyph");

            Assert.Contains("exclamation-indicator", indicator.Classes);
            Assert.Contains("workspace-entity-title", title.Classes);
            Assert.True(indicator.IsIndeterminate);
            Assert.Equal(1, glyph.Opacity);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardControl_HasUnreadAttentionFalse_ExclamationIndicatorIsHiddenWithoutLayoutShift()
    {
        var entity = CreateEntity();
        var control = new EntityCardControl { DataContext = new EntityCardViewModel(entity) };
        var window = new Window { Content = control, Width = 400, Height = 200 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var indicator = window.GetVisualDescendants()
                .OfType<ProgressBar>()
                .Single(progress => progress.Classes.Contains("exclamation-indicator"));
            var glyph = indicator.GetVisualDescendants()
                .OfType<TextBlock>()
                .Single(text => text.Name == "Glyph");
            var title = window.GetVisualDescendants()
                .OfType<SafeSelectableTextBlock>()
                .Single(text => text.Classes.Contains("workspace-entity-title"));
            var titleX = title.Bounds.X;

            Assert.False(indicator.IsIndeterminate);
            Assert.Equal(0, glyph.Opacity);

            entity.HasUnreadAttention = true;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(titleX, title.Bounds.X);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public async Task EntityCardViewModel_DisposedCard_DoesNotThrowOnLateHasUnreadAttentionChange()
    {
        var entity = CreateEntity();
        var card = new EntityCardViewModel(entity);
        await card.DisposeAsync();

        var exception = Record.Exception(() => entity.HasUnreadAttention = true);

        Assert.Null(exception);
    }

    private static async Task<MainWindowViewModel> CreateInitializedViewModelAsync()
    {
        var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        return viewModel;
    }

    private static async Task<EntityWorkspaceTabViewModel> OpenInactiveEntityTabAsync(
        MainWindowViewModel viewModel,
        SubscribedEntityViewModel entity,
        string tabId)
    {
        var tab = CreateTab(tabId, entity);
        await viewModel.OpenTabAsync(tab);
        await viewModel.OpenTabAsync(
            CreateTab(
                tabId + "-active",
                CreateEntity()));
        return tab;
    }

    private static EntityWorkspaceTabViewModel CreateTab(
        string tabId,
        SubscribedEntityViewModel entity)
        => new()
        {
            Id = tabId,
            Title = tabId,
            Entity = entity,
        };

    private static void NotifyUnread(MainWindowViewModel viewModel, string tabId)
    {
        viewModel.NotificationService.Notify(
            new Notification(
                new TabDescriptor { TabId = tabId },
                "Attention",
                "Unread test notification",
                DateTime.UtcNow,
                RunningState.Idle,
                NotificationState.Interesting));
    }

    private static SubscribedEntityViewModel CreateEntity()
    {
        var entityId = new EntityId(Guid.NewGuid());
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId.Value}}",
              "entity-types": ["entity", "task"],
              "display-name": { "default": "Attention entity" }
            }
            """);
        return new SubscribedEntityViewModel(
            new EntitySnapshot
            {
                EntityId = entityId,
                ConcurrencyTag = new ConcurrencyTag("1"),
                ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
                Data = document.RootElement.Clone(),
                Relationships = [],
            });
    }
}
