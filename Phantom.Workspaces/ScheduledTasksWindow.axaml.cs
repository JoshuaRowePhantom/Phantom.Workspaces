using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces;

public partial class ScheduledTasksWindow : Window
{
    // #1357: when the run-history list is scrolled within this distance (device pixels) of the
    // bottom, page in the next-older ~1-hour window.
    private const double LoadNextWindowThreshold = 48;

    public ScheduledTasksWindow()
    {
        InitializeComponent();

        // #1357: observe scrolling of the run-history TreeView (its internal ScrollViewer raises
        // the bubbling ScrollViewer.ScrollChanged event) so we can page in older history on demand.
        this.RunHistoryTree.AddHandler(ScrollViewer.ScrollChangedEvent, this.OnRunHistoryScrollChanged);
    }

    public ScheduledTasksWindow(ScheduledTasksViewModel viewModel)
        : this()
    {
        this.DataContext = viewModel;
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (this.DataContext is ScheduledTasksViewModel viewModel)
        {
            await viewModel.RefreshAsync();
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        this.Close();
    }

    private async void OnRunHistoryScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (this.DataContext is not ScheduledTasksViewModel viewModel
            || viewModel.SelectedToolRow is not { } row)
        {
            return;
        }

        var scrollViewer = (sender as Control)?.FindDescendantOfType<ScrollViewer>();
        if (scrollViewer is null)
        {
            return;
        }

        var distanceToBottom = scrollViewer.Extent.Height
            - (scrollViewer.Offset.Y + scrollViewer.Viewport.Height);
        if (distanceToBottom <= LoadNextWindowThreshold)
        {
            await row.LoadNextWindowAsync();
        }
    }
}
