using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Templates;
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
        AddDockDataTemplates();
        this.DataContext = viewModel;
        this.AddHandler(InputElement.KeyDownEvent, this.OnPreviewKeyDown, RoutingStrategies.Tunnel);
        this.AddHandler(InputElement.KeyUpEvent, this.OnPreviewKeyUp, RoutingStrategies.Tunnel);
        this.Deactivated += (_, _) =>
        {
            if (this.DataContext is MainWindowViewModel vm)
            {
                vm.IsAltHeld = false;
                vm.NavStackPopup.Dismiss();
            }
        };
    }

    private void AddDockDataTemplates()
    {
        foreach (var template in new DockDataTemplates())
        {
            this.DataTemplates.Add(template);
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (this.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            viewModel.NavStackPopup.OpenAtCurrentPosition();
            return;
        }

        if (e.Key is Key.LeftAlt or Key.RightAlt)
        {
            viewModel.IsAltHeld = true;
            return;
        }

        // Ctrl+Up / Ctrl+Down: move the nav-stack popup selection without navigating.
        if (viewModel.NavStackPopup.IsOpen && e.KeyModifiers == KeyModifiers.Control)
        {
            if (e.Key == Key.Up)
            {
                viewModel.NavStackPopup.MoveSelectionUp();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Down)
            {
                viewModel.NavStackPopup.MoveSelectionDown();
                e.Handled = true;
                return;
            }
        }

        // Ctrl+F7 / Ctrl+F8: non-interceptable notification navigation aliases.
        if (e.KeyModifiers == KeyModifiers.Control)
        {
            if (e.Key == Key.F7)
            {
                viewModel.NavigatePreviousNotificationCommand.Execute(null);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F8)
            {
                viewModel.NavigateNextNotificationCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Scroll)
        {
            if (viewModel.ActiveAgentViewModel is { } agent)
            {
                agent.AutoScrollEnabled = !agent.AutoScrollEnabled;
                e.Handled = true;
            }
            return;
        }

        // Ctrl+Shift+K: duplicate the active browser tab.
        if (e.Key == Key.K && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            viewModel.DuplicateBrowserTabCommand.Execute(null);
            e.Handled = true;
            return;
        }

        var index = GetDigitIndex(e.PhysicalKey);
        if (index < 0)
        {
            return;
        }

        if (e.KeyModifiers == KeyModifiers.Alt)
        {
            viewModel.GoToTabAtIndexCommand.Execute(index.ToString());
            e.Handled = true;
        }
        else if (e.KeyModifiers == (KeyModifiers.Alt | KeyModifiers.Shift))
        {
            viewModel.GoToWorkspacePaneAtIndexCommand.Execute(index.ToString());
            e.Handled = true;
        }
    }

    private static int GetDigitIndex(PhysicalKey key)
    {
        return key switch
        {
            PhysicalKey.Digit1 => 0,
            PhysicalKey.Digit2 => 1,
            PhysicalKey.Digit3 => 2,
            PhysicalKey.Digit4 => 3,
            PhysicalKey.Digit5 => 4,
            PhysicalKey.Digit6 => 5,
            PhysicalKey.Digit7 => 6,
            PhysicalKey.Digit8 => 7,
            PhysicalKey.Digit9 => 8,
            PhysicalKey.Digit0 => 9,
            _ => -1,
        };
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

        var settingsViewModel = new WorkspacesSettingsViewModel(
            persistenceService,
            configuration,
            viewModel,
            (Application.Current as App)?.UpdateController,
            action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
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

    private ScheduledTasksWindow? openScheduledTasksWindow;
    private GitWorkspacesWindow? openGitWorkspacesWindow;

    private async void OnOpenScheduledTasksClicked(
        object? sender,
        RoutedEventArgs e)
    {
        if (this.openScheduledTasksWindow is { } existing)
        {
            existing.Activate();
            return;
        }

        if (this.DataContext is not MainWindowViewModel viewModel
            || viewModel.TryCreateScheduledTasksViewModel() is not { } scheduledTasksViewModel)
        {
            return;
        }

        await scheduledTasksViewModel.RefreshAsync();
        var scheduledTasksWindow = new ScheduledTasksWindow(scheduledTasksViewModel);
        this.openScheduledTasksWindow = scheduledTasksWindow;
        await scheduledTasksWindow.ShowDialog(this);
        this.openScheduledTasksWindow = null;
        scheduledTasksViewModel.Dispose();
    }

    private async void OnOpenGitWorkspacesClicked(
        object? sender,
        RoutedEventArgs e)
    {
        if (this.openGitWorkspacesWindow is { } existing)
        {
            existing.Activate();
            return;
        }

        if (this.DataContext is not MainWindowViewModel viewModel
            || viewModel.TryCreateGitWorkspacesViewModel() is not { } gitWorkspacesViewModel)
        {
            return;
        }

        await gitWorkspacesViewModel.RefreshAsync();
        var gitWorkspacesWindow = new GitWorkspacesWindow(gitWorkspacesViewModel);
        this.openGitWorkspacesWindow = gitWorkspacesWindow;
        await gitWorkspacesWindow.ShowDialog(this);
        this.openGitWorkspacesWindow = null;
    }

    private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        if (this.DataContext is not MainWindowViewModel viewModel) return;

        if (e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            var historyIndex = viewModel.NavStackPopup.CommitAndBeginFade();
            if (historyIndex >= 0)
            {
                viewModel.NavigateToHistoryEntry(historyIndex);
            }
            return;
        }

        if (e.Key is Key.LeftAlt or Key.RightAlt)
            viewModel.IsAltHeld = false;
    }
}