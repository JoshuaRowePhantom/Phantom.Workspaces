using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Templates;

public partial class EntityBrowserWorkspaceTabView : UserControl
{
    public EntityBrowserWorkspaceTabView()
    {
        AvaloniaXamlLoader.Load(this);
        this.DataContextChanged += this.OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (this.DataContext is EntityBrowserWorkspaceTabViewModel vm)
        {
            // Rebind Find with a bringIntoView callback that scrolls the corresponding
            // TreeViewItem into view within the browser's TreeView.
            vm.Find = new FindViewModel(vm.EntityList, this.BringCardIntoView);
        }
    }

    private void BringCardIntoView(EntityCardViewModel card)
    {
        var tree = this.FindControl<TreeView>("BrowserTreeView");
        if (tree is null || tree.ItemsSource is null)
        {
            return;
        }

        foreach (var item in tree.ItemsSource)
        {
            if (item is EntityListItemViewModel listItem && listItem.Card == card)
            {
                if (tree.ContainerFromItem(item) is TreeViewItem treeItem)
                {
                    treeItem.BringIntoView();
                }
                return;
            }
        }
    }
}


