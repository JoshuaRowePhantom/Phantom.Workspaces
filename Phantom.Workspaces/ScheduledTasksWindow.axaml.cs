using Avalonia.Controls;
using Avalonia.Interactivity;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces;

public partial class ScheduledTasksWindow : Window
{
    public ScheduledTasksWindow()
    {
        InitializeComponent();
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
}
