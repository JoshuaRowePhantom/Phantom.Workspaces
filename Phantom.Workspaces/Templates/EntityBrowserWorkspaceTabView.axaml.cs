using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Templates;

public partial class EntityBrowserWorkspaceTabView : UserControl
{
    private readonly ItemsControl browserItemsControl;
    private ScrollViewer? viewportScrollViewer;
    private Visual? viewportVisual;

    public EntityBrowserWorkspaceTabView()
    {
        AvaloniaXamlLoader.Load(this);
        this.browserItemsControl = this.FindControl<ItemsControl>("BrowserItemsControl")
            ?? throw new InvalidOperationException("Entity browser items control was not found.");

        this.AttachedToVisualTree += this.OnAttachedToVisualTree;
        this.DetachedFromVisualTree += this.OnDetachedFromVisualTree;
        this.LayoutUpdated += this.OnLayoutUpdated;
        this.DataContextChanged += this.OnDataContextChanged;
    }

    private void OnViewportScrollChanged(
        object? sender,
        ScrollChangedEventArgs e)
    {
        this.UpdateStickyContext();
    }

    private void OnAttachedToVisualTree(
        object? sender,
        VisualTreeAttachmentEventArgs e)
    {
        this.EnsureViewportSubscription();
        this.UpdateStickyContext();
    }

    private void OnDetachedFromVisualTree(
        object? sender,
        VisualTreeAttachmentEventArgs e)
    {
        this.ClearViewportSubscription();
    }

    private void OnLayoutUpdated(
        object? sender,
        EventArgs e)
    {
        this.EnsureViewportSubscription();
        this.UpdateStickyContext();
    }

    private void OnDataContextChanged(
        object? sender,
        EventArgs e)
    {
        this.UpdateStickyContext();
    }

    private void UpdateStickyContext()
    {
        if (this.DataContext is not EntityBrowserWorkspaceTabViewModel viewModel)
        {
            return;
        }

        var viewport = this.viewportVisual;
        if (viewport is null)
        {
            return;
        }

        var itemMap = viewModel.EntityList.Items
            .ToDictionary(item => item.ItemKey, StringComparer.Ordinal);
        var parentByItemKey = itemMap.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.ParentItemKey,
            StringComparer.Ordinal);
        var presenterByItemKey = new Dictionary<string, ContentPresenter>(StringComparer.Ordinal);
        foreach (var presenter in this.browserItemsControl.GetVisualDescendants().OfType<ContentPresenter>())
        {
            if (presenter.DataContext is not EntityListItemViewModel item)
            {
                continue;
            }

            presenterByItemKey[item.ItemKey] = presenter;
        }

        foreach (var presenter in presenterByItemKey.Values)
        {
            this.SetStickyOffset(presenter, 0);
            this.SetPinnedVisualState(presenter, isPinned: false);
        }

        var visibleItems = new List<VisibleEntityListItemPosition>();
        foreach (var pair in presenterByItemKey)
        {
            var presenter = pair.Value;
            var translatedPoint = presenter.TranslatePoint(new Point(0, 0), viewport);
            if (translatedPoint is null)
            {
                continue;
            }

            var top = translatedPoint.Value.Y;
            var bottom = top + presenter.Bounds.Height;
            visibleItems.Add(new VisibleEntityListItemPosition(pair.Key, top, bottom));
        }

        if (visibleItems.Count == 0)
        {
            viewModel.UpdateStickyContextFromVisibleItem(null);
            return;
        }

        var heightsByItemKey = presenterByItemKey.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.Bounds.Height,
            StringComparer.Ordinal);
        var stickyLayout = EntityBrowserStickyContextSelector.SelectPinnedItems(
            visibleItems,
            parentByItemKey,
            heightsByItemKey);
        viewModel.UpdateStickyContextFromVisibleItem(stickyLayout.FocusedItemKey);

        foreach (var pinnedItem in stickyLayout.PinnedItems)
        {
            if (!presenterByItemKey.TryGetValue(pinnedItem.ItemKey, out var presenter))
            {
                continue;
            }

            var translatedPoint = presenter.TranslatePoint(new Point(0, 0), viewport);
            if (translatedPoint is null)
            {
                continue;
            }

            this.SetStickyOffset(presenter, pinnedItem.Top - translatedPoint.Value.Y);
            this.SetPinnedVisualState(presenter, isPinned: true);
        }
    }

    private void EnsureViewportSubscription()
    {
        var resolvedScrollViewer = this.browserItemsControl.GetVisualAncestors()
            .OfType<ScrollViewer>()
            .FirstOrDefault();
        var resolvedViewport = (Visual?)resolvedScrollViewer
            ?? this.browserItemsControl.GetVisualAncestors()
                .OfType<Control>()
                .FirstOrDefault(static control => control.ClipToBounds)
            ?? this.browserItemsControl;

        if (ReferenceEquals(this.viewportVisual, resolvedViewport)
            && ReferenceEquals(this.viewportScrollViewer, resolvedScrollViewer))
        {
            return;
        }

        this.ClearViewportSubscription();
        this.viewportVisual = resolvedViewport;
        this.viewportScrollViewer = resolvedScrollViewer;
        if (this.viewportScrollViewer is not null)
        {
            this.viewportScrollViewer.ScrollChanged += this.OnViewportScrollChanged;
        }
    }

    private void ClearViewportSubscription()
    {
        if (this.viewportScrollViewer is not null)
        {
            this.viewportScrollViewer.ScrollChanged -= this.OnViewportScrollChanged;
        }

        this.viewportScrollViewer = null;
        this.viewportVisual = null;
    }

    private void SetStickyOffset(
        Visual visual,
        double yOffset)
    {
        if (visual.RenderTransform is TranslateTransform translateTransform)
        {
            translateTransform.Y = yOffset;
            return;
        }

        visual.RenderTransform = new TranslateTransform { Y = yOffset };
    }

    private void SetPinnedVisualState(
        ContentPresenter presenter,
        bool isPinned)
    {
        if (presenter.DataContext is EntityListItemViewModel item)
        {
            presenter.SetValue(Panel.ZIndexProperty, isPinned ? 1000 - item.Level : 0);
        }
        else
        {
            presenter.SetValue(Panel.ZIndexProperty, isPinned ? 1000 : 0);
        }

        var cardBorder = presenter.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(static border => border.Classes.Contains("entity-card"));
        if (cardBorder is null)
        {
            return;
        }

        if (isPinned)
        {
            cardBorder.Classes.Add("context-pinned");
        }
        else
        {
            cardBorder.Classes.Remove("context-pinned");
        }
    }
}
