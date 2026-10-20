using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.Templates;
using Phantom.Workspaces.ViewModels;
using Phantom.Workspaces.ViewModels.Configuration;

namespace Phantom.Workspaces;

public partial class MainWindow : Window
{
    private AccessHostServices? accessHost;

    public void AttachAccessHost(AccessHostServices host, AuthorizationRequestsPopupViewModel popup)
    {
        if (!ReferenceEquals(host.AuthorizationRows, popup.Rows))
        {
            throw new ArgumentException("The popup must use the process authorization rows.", nameof(popup));
        }
        this.accessHost = host;
        this.AttachAuthorizationRequests(popup);
    }

    public void AttachAuthorizationRequests(AuthorizationRequestsPopupViewModel popup)
    {
        popup.Rows.SetPresentationMode(AuthorizationRowsPresentationMode.Runtime);
        this.AuthorizationRequestsHost.DataContext = popup;
        this.AuthorizationRequestsHost.IsVisible = true;
    }

    private void OnAuthorizationKeyClicked(object? sender, RoutedEventArgs arguments)
    {
        (this.AuthorizationRequestsHost.DataContext as AuthorizationRequestsPopupViewModel)?.ToggleOpen();
    }

    private void OnAuthorizationPopupKeyDown(object? sender, KeyEventArgs arguments)
    {
        if (arguments.Key != Key.Escape) { return; }
        (this.AuthorizationRequestsHost.DataContext as AuthorizationRequestsPopupViewModel)?.Dismiss();
        this.AuthorizationKey.Focus();
        arguments.Handled = true;
    }

    private void OnAuthorizationPopupClosed(object? sender, EventArgs arguments)
    {
        (this.AuthorizationRequestsHost.DataContext as AuthorizationRequestsPopupViewModel)?.NotifyLightDismissed();
        this.AuthorizationKey.Focus();
    }

    private bool ctrlIsDown;
    private bool navStackIntentSeenWhileCtrlDown;

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
        viewModel.OpenAccessAdministrationRequested += this.OnOpenAccessAdministrationRequested;
        this.Closed += (_, _) => viewModel.OpenAccessAdministrationRequested -= this.OnOpenAccessAdministrationRequested;
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
        // #1196: construct DockDataTemplates ONCE and share the same IDataTemplate
        // instances with Window scope AND TopLevelDockControl scope. Sharing (rather
        // than constructing twice) makes the top-level DockControl's DataTemplates
        // the single source floating PhantomHostWindows reuse via
        // IFactory.DockControls. See #1119 for why both scopes are required.
        var templates = new DockDataTemplates();
        foreach (var template in templates)
        {
            this.DataTemplates.Add(template);
            this.TopLevelDockControl.DataTemplates.Add(template);
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape &&
            this.AuthorizationRequestsHost.DataContext is AuthorizationRequestsPopupViewModel { IsOpen: true } authorizationPopup)
        {
            authorizationPopup.Dismiss();
            this.AuthorizationKey.Focus();
            e.Handled = true;
            return;
        }

        if (this.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            this.ctrlIsDown = true;
            this.navStackIntentSeenWhileCtrlDown = false;
            return;
        }

        if (this.ctrlIsDown
            && (e.Key is Key.Tab or Key.Up or Key.Down)
            && (e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            if (!viewModel.NavStackPopup.IsOpen)
            {
                viewModel.NavStackPopup.OpenAtCurrentPosition();
            }

            this.navStackIntentSeenWhileCtrlDown = true;
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

        // Ctrl+F: open the find bar and focus + select-all the find input.
        // #1199: defer focus/select-all past the IsVisible binding and layout pass, since the
        // FindTextBox is invisible until Find.IsOpen flips true. Focus() on an unattached
        // control does not reliably move focus. SelectAll() also missing before, so re-Ctrl-F
        // on an already-open bar now re-selects text so typing replaces the query.
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            viewModel.Find.OpenCommand.Execute(null);

            Avalonia.Threading.Dispatcher.UIThread.Post(
                () =>
                {
                    var findTextBox = this.FindControl<TextBox>("FindTextBox");
                    if (findTextBox is null)
                    {
                        return;
                    }
                    findTextBox.Focus();
                    findTextBox.SelectAll();
                },
                Avalonia.Threading.DispatcherPriority.Input);

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

        var settingsViewModel = await viewModel.CreateSettingsDialogViewModelAsync();
        var settingsWindow = new SettingsDialogWindow(settingsViewModel);

        await settingsWindow.ShowDialog(this);
        if (settingsWindow.Result is { } configuration)
        {
            await viewModel.ApplyRemoteAccessChangeAsync(
                configuration.RemoteHosting,
                configuration.DevTunnel);
        }
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

    private void OnOpenAccessAdministrationClicked(
        object? sender,
        RoutedEventArgs e)
    {
        if (this.DataContext is MainWindowViewModel viewModel)
        {
            viewModel.OpenAccessAdministrationCommand.Execute(null);
        }
    }

    private async void OnOpenAccessAdministrationRequested()
    {
        await this.OpenAccessAdministrationAsync();
    }

    /// <summary>The most recently opened access-administration window.</summary>
    internal AccessAdministrationWindow? LastAccessAdministrationWindow { get; private set; }

    private async Task OpenAccessAdministrationAsync()
    {
        if (this.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var host = this.accessHost ?? throw new InvalidOperationException("The process access host has not been attached.");
        if (this.LastAccessAdministrationWindow is { } existing)
        {
            existing.Activate();
            return;
        }
        await using var window = new AccessAdministrationWindow(host);
        this.LastAccessAdministrationWindow = window;
        try
        {
            await window.RefreshAsync();
            if (!window.IsDisposed) { await window.ShowDialog(this); }
        }
        finally
        {
            this.LastAccessAdministrationWindow = null;
        }
    }

    private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        if (this.DataContext is not MainWindowViewModel viewModel) return;

        if (e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            this.ctrlIsDown = false;
            if (this.navStackIntentSeenWhileCtrlDown)
            {
                var historyIndex = viewModel.NavStackPopup.CommitAndBeginFade();
                if (historyIndex >= 0)
                {
                    viewModel.NavigateToHistoryEntry(historyIndex);
                }
            }

            this.navStackIntentSeenWhileCtrlDown = false;
            return;
        }

    }
}
