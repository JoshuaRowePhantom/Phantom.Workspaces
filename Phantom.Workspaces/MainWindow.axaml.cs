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
                vm.NavStackPopup.Dismiss();
            }
        };
    }

    private void AddDockDataTemplates()
    {
        // The DockDataTemplates must be applied both at Window scope (for content resolution)
        // and directly on the top-level DockControl (so Dock.Avalonia's tab-strip rendering
        // scope, which does not walk up to ancestor DataTemplates, can resolve the custom
        // WorkspacesPaneDock header template + glyph indicator templates). See #1119.
        foreach (var template in new DockDataTemplates())
        {
            this.DataTemplates.Add(template);
        }
        foreach (var template in new DockDataTemplates())
        {
            this.TopLevelDockControl.DataTemplates.Add(template);
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

        // Ctrl+F: open the find bar and focus the find input.
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            viewModel.Find.OpenCommand.Execute(null);
            var findTextBox = this.FindControl<TextBox>("FindTextBox");
            findTextBox?.Focus();
            e.Handled = true;
            return;
        }

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
            action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
            viewModel.LogDirectoryProvider,
            new Phantom.Workspaces.Install.RealProcessLauncher());
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

    }
}
