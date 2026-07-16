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
        }

        this.subscribedViewModel = this.DataContext as AgentViewModel;
        if (this.subscribedViewModel is not null)
        {
            this.subscribedViewModel.OpenLogWindowRequested += this.OnOpenLogWindowRequested;
        }
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

