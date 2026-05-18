using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Templates;

public partial class EntityBrowserWorkspaceTabView : UserControl
{
    private readonly ScrollViewer browserScrollViewer;
    private readonly ItemsControl browserItemsControl;

    public EntityBrowserWorkspaceTabView()
    {
        AvaloniaXamlLoader.Load(this);
        this.browserScrollViewer = this.FindControl<ScrollViewer>("BrowserScrollViewer")
            ?? throw new InvalidOperationException("Entity browser scroll viewer was not found.");
        this.browserItemsControl = this.FindControl<ItemsControl>("BrowserItemsControl")
            ?? throw new InvalidOperationException("Entity browser items control was not found.");

        this.browserScrollViewer.ScrollChanged += this.OnBrowserScrollChanged;
        this.LayoutUpdated += this.OnLayoutUpdated;
        this.DataContextChanged += this.OnDataContextChanged;
    }

    private void OnBrowserScrollChanged(
        object? sender,
        ScrollChangedEventArgs e)
    {
        this.UpdateStickyContext();
    }

    private void OnLayoutUpdated(
        object? sender,
        EventArgs e)
    {
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

        var visibleItems = new List<VisibleEntityListItemPosition>();
        foreach (var presenter in this.browserItemsControl.GetVisualDescendants().OfType<ContentPresenter>())
        {
            if (presenter.DataContext is not EntityListItemViewModel item)
            {
                continue;
            }

            var translatedPoint = presenter.TranslatePoint(new Point(0, 0), this.browserScrollViewer);
            if (translatedPoint is null)
            {
                continue;
            }

            var top = translatedPoint.Value.Y;
            var bottom = top + presenter.Bounds.Height;
            visibleItems.Add(new VisibleEntityListItemPosition(item.ItemKey, top, bottom));
        }

        var focusedItemKey = EntityBrowserStickyContextSelector.SelectFocusedItemKey(visibleItems);
        viewModel.UpdateStickyContextFromVisibleItem(focusedItemKey);
    }
}
