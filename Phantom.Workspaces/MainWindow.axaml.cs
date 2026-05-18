using Avalonia.Controls;
using Avalonia.Interactivity;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces;

public partial class MainWindow : Window
{
    public MainWindow()
        : this(new MainWindowViewModel(new RepositorySource(RepositorySourceType.Unknown, "(none)")))
    {
    }

    public MainWindow(
        MainWindowViewModel viewModel)
    {
        InitializeComponent();
        this.DataContext = viewModel;
    }

    private async void OnOpenSettingsClicked(
        object? sender,
        RoutedEventArgs e)
    {
        if (this.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var settingsWindow = new SettingsWindow
        {
            DataContext = viewModel,
        };

        await settingsWindow.ShowDialog(this);
    }
}