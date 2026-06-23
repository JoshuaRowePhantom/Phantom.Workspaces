using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.ViewModels;
using Phantom.Workspaces.ViewModels.Configuration;

namespace Phantom.Workspaces;

public partial class MainWindow : Window
{
    public MainWindow()
        : this(new MainWindowViewModel(new UnknownRepositorySource()))
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

        // Open the unified master-detail settings dialog (Repository, Remote access, Appearance) built
        // from the persisted configuration, with the live profile theme/debugging folded in as a
        // Profile section so nothing from the legacy settings window is lost.
        var persistenceService = new ConfigurationPersistenceService();
        var configuration = persistenceService.ConfigurationExists()
            ? await persistenceService.LoadAsync()
            : new WorkspacesConfiguration();

        var settingsViewModel = new WorkspacesSettingsViewModel(persistenceService, configuration, viewModel);
        var settingsWindow = new SettingsDialogWindow(settingsViewModel);

        await settingsWindow.ShowDialog(this);
    }

    private void OnOpenConnectionStatusClicked(
        object? sender,
        RoutedEventArgs e)
    {
        if (this.DataContext is not MainWindowViewModel viewModel || viewModel.ConnectionStatus is null)
        {
            return;
        }

        var statusWindow = new ConnectionStatusWindow(viewModel.ConnectionStatus);
        statusWindow.ShowDialog(this);
    }

    private async void OnOpenScheduledTasksClicked(
        object? sender,
        RoutedEventArgs e)
    {
        if (this.DataContext is not MainWindowViewModel viewModel
            || viewModel.TryCreateScheduledTasksViewModel() is not { } scheduledTasksViewModel)
        {
            return;
        }

        await scheduledTasksViewModel.RefreshAsync();
        var scheduledTasksWindow = new ScheduledTasksWindow(scheduledTasksViewModel);
        await scheduledTasksWindow.ShowDialog(this);
    }
}