using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using System.Linq;
using Phantom.Workspaces.Agent.Gui.ViewModels;

namespace Phantom.Workspaces.Agent.Gui.Controls;

public partial class AgentChatEditorControl : UserControl
{
    private static readonly GridLength DefaultExpandedTreeWidth = new(280);
    private static readonly GridLength ExpandedSplitterWidth = new(24);
    private static readonly GridLength CollapsedWidth = new(0);
    private GridLength expandedTreeWidth = DefaultExpandedTreeWidth;

    public AgentChatEditorControl()
    {
        this.InitializeComponent();
    }

    private void OnEditorSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not TreeView { SelectedItem: AgentEditorNavigationItemViewModel selected } || this.DataContext is not AgentViewModel vm)
        {
            return;
        }

        vm.SelectedEditorItem = selected;
    }

    private void OnTreeCollapseToggleClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not ToggleButton button)
        {
            return;
        }

        this.SetTreeCollapsed(button.IsChecked == true);
    }

    private void SetTreeCollapsed(bool collapsed)
    {
        if (this.EditorGrid.ColumnDefinitions.Count >= 3)
        {
            if (collapsed)
            {
                if (this.EditorGrid.ColumnDefinitions[0].Width.Value > 0)
                {
                    this.expandedTreeWidth = this.EditorGrid.ColumnDefinitions[0].Width;
                }

                this.EditorGrid.ColumnDefinitions[0].Width = CollapsedWidth;
                this.EditorGrid.ColumnDefinitions[1].Width = CollapsedWidth;
            }
            else
            {
                this.EditorGrid.ColumnDefinitions[0].Width = this.expandedTreeWidth.Value > 0 ? this.expandedTreeWidth : DefaultExpandedTreeWidth;
                this.EditorGrid.ColumnDefinitions[1].Width = ExpandedSplitterWidth;
            }
        }

        this.NavigationTree.IsVisible = !collapsed;
        this.SplitterHost.IsVisible = !collapsed;
        this.TreeCollapseToggle.Content = collapsed ? "▶" : "◀";
        if (this.TreeCollapseToggle.IsChecked != collapsed)
        {
            this.TreeCollapseToggle.IsChecked = collapsed;
        }

        if (collapsed && this.DataContext is AgentViewModel vm && vm.EditorItems.FirstOrDefault() is { } root)
        {
            vm.SelectedEditorItem = root;
        }
    }
}
