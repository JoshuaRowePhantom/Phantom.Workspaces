using Avalonia.Controls;
using Avalonia.Interactivity;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces;

public partial class GitWorkspacesWindow : Window
{
    public GitWorkspacesWindow()
    {
        InitializeComponent();
    }

    public GitWorkspacesWindow(GitWorkspacesViewModel viewModel)
        : this()
    {
        this.DataContext = viewModel;
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (this.DataContext is GitWorkspacesViewModel viewModel)
        {
            await viewModel.RefreshAsync();
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        this.Close();
    }
}
