using Avalonia.Controls;
using Avalonia.Interactivity;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces;

public partial class ConnectionStatusWindow : Window
{
    public ConnectionStatusWindow()
    {
        InitializeComponent();
    }

    public ConnectionStatusWindow(ConnectionStatusViewModel viewModel)
        : this()
    {
        this.DataContext = viewModel;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        this.Close();
    }
}
