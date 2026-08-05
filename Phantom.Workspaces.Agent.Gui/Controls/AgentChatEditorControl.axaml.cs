using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using Phantom.Workspaces.Agent.Gui.ViewModels;

namespace Phantom.Workspaces.Agent.Gui.Controls;

public partial class AgentChatEditorControl : UserControl
{
    private static readonly GridLength DefaultExpandedTreeWidth = new(280);
    private static readonly GridLength ExpandedSplitterWidth = new(24);
    private static readonly GridLength CollapsedWidth = new(0);
    private const double ExpandedTreeMinWidth = 160;
    private GridLength expandedTreeWidth = DefaultExpandedTreeWidth;
    private AgentViewModel? subscribedViewModel;
    private LogWindow? logWindow;

    public AgentChatEditorControl()
    {
        this.InitializeComponent();
        this.DataContextChanged += this.OnDataContextChanged;

        // The navigation pane starts collapsed so the chat output uses the full width by
        // default; the user expands it on demand via the collapse toggle.
        this.SetTreeCollapsed(true);
    }

    private void OpenLogWindow()
    {
        if (this.DataContext is not AgentViewModel viewModel)
        {
            return;
        }

        if (this.logWindow is not null)
        {
            this.logWindow.Activate();
            return;
        }

        this.logWindow = new LogWindow(viewModel.LoggerFactory);
        this.logWindow.Closed += (_, _) => this.logWindow = null;

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is not null)
        {
            this.logWindow.Show(owner);
            return;
        }

        this.logWindow.Show();
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (this.subscribedViewModel is not null)
        {
            this.subscribedViewModel.OpenLogWindowRequested -= this.OnOpenLogWindowRequested;
            this.subscribedViewModel.PropertyChanged -= this.OnViewModelPropertyChanged;
        }

        this.subscribedViewModel = this.DataContext as AgentViewModel;
        if (this.subscribedViewModel is not null)
        {
            this.subscribedViewModel.OpenLogWindowRequested += this.OnOpenLogWindowRequested;
            this.subscribedViewModel.PropertyChanged += this.OnViewModelPropertyChanged;
            // Issue #1111: on initial bind the VM may already carry a selection (e.g. the root),
            // so run the ancestor-expansion pass once here in addition to the PropertyChanged path.
            this.ExpandAncestorsOfSelectedItem(this.subscribedViewModel);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Issue #1111: when SelectedEditorItem changes programmatically (initial selection,
        // tree collapse, sub-agent jump-button navigation via NavigateToAgentHandler), make sure
        // every ancestor of the target is expanded so its TreeViewItem container is materialised.
        // The two-way SelectedItem binding then applies IsSelected = true to the realised
        // container, which is what the shared entity-card .selected style keys the blue border
        // recolour off.
        if (e.PropertyName != nameof(AgentViewModel.SelectedEditorItem))
        {
            return;
        }

        if (sender is AgentViewModel vm)
        {
            this.ExpandAncestorsOfSelectedItem(vm);
        }
    }

    private void ExpandAncestorsOfSelectedItem(AgentViewModel vm)
    {
        var target = vm.SelectedEditorItem;
        if (target is null)
        {
            return;
        }

        foreach (var root in vm.EditorItems)
        {
            var ancestors = new List<AgentEditorNavigationItemViewModel>();
            if (TryBuildAncestorPath(root, target, ancestors))
            {
                // ancestors are the nodes from the root down to (but excluding) the target itself;
                // expand each so containers materialise in top-down order.
                foreach (var ancestor in ancestors)
                {
                    ancestor.IsExpanded = true;
                }

                return;
            }
        }
    }

    private static bool TryBuildAncestorPath(
        AgentEditorNavigationItemViewModel node,
        AgentEditorNavigationItemViewModel target,
        List<AgentEditorNavigationItemViewModel> ancestors)
    {
        if (ReferenceEquals(node, target))
        {
            return true;
        }

        ancestors.Add(node);
        foreach (var child in node.Children)
        {
            if (TryBuildAncestorPath(child, target, ancestors))
            {
                return true;
            }
        }
        ancestors.RemoveAt(ancestors.Count - 1);
        return false;
    }

    private void OnOpenLogWindowRequested(object? sender, System.EventArgs e)
    {
        this.OpenLogWindow();
    }

    private void OnEditorSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not TreeView { SelectedItem: AgentEditorNavigationItemViewModel selected } || this.DataContext is not AgentViewModel vm)
        {
            return;
        }

        vm.SelectedEditorItem = selected;
    }

    private void OnNavigationTreePointerEntered(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (this.DataContext is AgentViewModel vm)
        {
            vm.SubAgentsContainer.SuppressSort();
        }
    }

    private void OnNavigationTreePointerExited(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (this.DataContext is AgentViewModel vm)
        {
            vm.SubAgentsContainer.ScheduleResumeSort();
        }
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

                // Issue #1051: relax the tree column's drag-clamp floor so the intentional
                // programmatic full-collapse can still drive the column to zero width.
                this.EditorGrid.ColumnDefinitions[0].MinWidth = 0;
                this.EditorGrid.ColumnDefinitions[0].Width = CollapsedWidth;
                this.EditorGrid.ColumnDefinitions[1].Width = CollapsedWidth;
            }
            else
            {
                // Issue #1051: restore the 160px floor so a drag can never collapse the tree
                // pane (and hide the splitter behind the native output) while it is shown.
                this.EditorGrid.ColumnDefinitions[0].MinWidth = ExpandedTreeMinWidth;
                this.EditorGrid.ColumnDefinitions[0].Width = this.expandedTreeWidth.Value > 0 ? this.expandedTreeWidth : DefaultExpandedTreeWidth;
                this.EditorGrid.ColumnDefinitions[1].Width = ExpandedSplitterWidth;
            }
        }

        this.NavigationTree.IsVisible = !collapsed;
        this.SplitterHost.IsVisible = !collapsed;
        // Issue #1120: the ">>"/"<<" glyph is now driven by the shared pane-collapser style's
        // :checked state trigger, not by a code-behind Content swap.
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

