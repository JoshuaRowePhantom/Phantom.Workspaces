using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Templates;
using Phantom.Workspaces.ViewModels;
using Phantom.Workspaces.ViewModels.Configuration;

namespace Phantom.Workspaces;

public partial class MainWindow : Window
{
    private AccessHostServices? accessHost;
    private readonly System.Collections.Generic.List<Task> accessAdministrationDisposals = [];
    private bool accessAdministrationShuttingDown;
    private AuthorizationRequestsPopupViewModel? authorizationRequests;
    private AccessPresentationOwnerIdentifier? authorizationPresentationOwner;
    private bool authorizationShellOpened;
    internal Task AccessAdministrationReady { get; private set; } = Task.CompletedTask;
    internal Func<AccessHostServices, AccessAdministrationWindow> AccessAdministrationWindowFactory { get; init; } =
        host => new AccessAdministrationWindow(host);

    public void AttachAccessHost(
        AccessHostServices host,
        AuthorizationRequestsPopupViewModel popup,
        AccessPresentationOwnerIdentifier? presentationOwner = null)
    {
        if (!ReferenceEquals(host.AuthorizationRows, popup.Rows))
        {
            throw new ArgumentException("The popup must use the process authorization rows.", nameof(popup));
        }
        this.accessHost = host;
        this.authorizationPresentationOwner = presentationOwner ?? host.PresentationOwner;
        host.EnterRuntime();
        this.AttachAuthorizationRequests(popup);
    }

    public void AttachAuthorizationRequests(AuthorizationRequestsPopupViewModel popup)
    {
        this.DetachAuthorizationPresentation();
        this.authorizationRequests = popup;
        popup.Rows.NewActionablePrompt += this.OnNewAuthorizationPrompt;
        this.AuthorizationRequestsHost.DataContext = popup;
        this.AuthorizationRequestsHost.IsVisible = true;
        if (this.IsVisible) { this.StartAuthorizationPresentation(); }
    }

    private void StartAuthorizationPresentation()
    {
        if (this.accessAdministrationShuttingDown || this.authorizationRequests is not { } popup ||
            !this.IsVisible || !this.authorizationShellOpened) { return; }
        popup.Rows.NotifyPresentationOwnerAvailabilityChanged();
        popup.Rows.SetPresentationMode(AuthorizationRowsPresentationMode.Runtime);
    }

    private void OnNewAuthorizationPrompt(object? sender, AuthorizationPromptEventArgs arguments)
    {
        if (arguments.Presentation is not null || this.accessAdministrationShuttingDown || !this.IsVisible || !this.authorizationShellOpened ||
            arguments.PresentationOwner is { } owner && owner != this.authorizationPresentationOwner) { return; }
        if (this.accessHost is not null)
        {
            arguments.Presentation = this.BeginOpenAccessAdministration(CredentialAccessPage.Requests, activate: false);
        }
        else if (this.authorizationRequests is { } popup)
        {
            popup.ShowRequests();
            arguments.Presentation = Task.FromResult(true);
        }
    }

    private void DetachAuthorizationPresentation()
    {
        if (this.authorizationRequests is { } popup)
        {
            popup.Rows.NewActionablePrompt -= this.OnNewAuthorizationPrompt;
            // A hidden or closed shell must not hold any undelivered prompt.
            popup.Rows.NotifyPresentationOwnerAvailabilityChanged();
            popup.Rows.PresentFreshPrompts();
        }
    }

    private void OnAuthorizationPopupKeyDown(object? sender, KeyEventArgs arguments)
    {
        if (arguments.Key != Key.Escape) { return; }
        (this.AuthorizationRequestsHost.DataContext as AuthorizationRequestsPopupViewModel)?.Dismiss();
        this.CredentialAccessKey.Focus();
        arguments.Handled = true;
    }

    private void OnAuthorizationPopupClosed(object? sender, EventArgs arguments)
    {
        (this.AuthorizationRequestsHost.DataContext as AuthorizationRequestsPopupViewModel)?.NotifyLightDismissed();
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
        this.Opened += (_, _) =>
        {
            if (this.authorizationShellOpened) { return; }
            this.authorizationShellOpened = true;
            this.StartAuthorizationPresentation();
        };
        this.PropertyChanged += (_, arguments) =>
        {
            if (arguments.Property != IsVisibleProperty) { return; }
            if (this.IsVisible) { this.StartAuthorizationPresentation(); }
            else if (this.authorizationRequests is { } popup)
            {
                popup.Rows.NotifyPresentationOwnerAvailabilityChanged();
                popup.Rows.PresentFreshPrompts();
            }
        };
        this.Closed += (_, _) =>
        {
            viewModel.OpenAccessAdministrationRequested -= this.OnOpenAccessAdministrationRequested;
            this.accessAdministrationShuttingDown = true;
            this.DetachAuthorizationPresentation();
            this.LastAccessAdministrationWindow?.Close();
        };
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
            this.CredentialAccessKey.Focus();
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
        if (this.accessHost is null && this.authorizationRequests is { } popup)
        {
            popup.ToggleOpen();
            return;
        }
        if (this.DataContext is MainWindowViewModel viewModel)
        {
            viewModel.OpenAccessAdministrationCommand.Execute(null);
        }
    }

    private void OnOpenAccessAdministrationRequested()
    {
        this.BeginOpenAccessAdministration(this.authorizationRequests?.ActionableCount > 0
            ? CredentialAccessPage.Requests : CredentialAccessPage.SavedApprovals);
    }

    private Task<bool> BeginOpenAccessAdministration(CredentialAccessPage page, bool activate = true)
    {
        var opening = this.OpenAccessAdministrationAsync(page, activate);
        this.AccessAdministrationReady = Task.WhenAll(this.AccessAdministrationReady, opening);
        return opening;
    }

    /// <summary>The most recently opened access-administration window.</summary>
    internal AccessAdministrationWindow? LastAccessAdministrationWindow { get; private set; }

    private async Task<bool> OpenAccessAdministrationAsync(CredentialAccessPage page, bool activate)
    {
        if (this.DataContext is not MainWindowViewModel || this.accessAdministrationShuttingDown)
        {
            return false;
        }

        if (this.accessHost is not { } host)
        {
            this.ReportAccessAdministrationFailure();
            return false;
        }
        AccessAdministrationWindow? window = this.LastAccessAdministrationWindow;
        var shown = window?.IsVisible == true;
        try
        {
            if (window is null)
            {
                window = this.AccessAdministrationWindowFactory(host);
                window.NavigateEntityAsync = reference => this.DataContext is MainWindowViewModel navigation
                    ? navigation.OpenEntityByIdAsync(reference.Identifier.ToString("D")) : Task.CompletedTask;
                this.LastAccessAdministrationWindow = window;
                window.SelectPage(page);
                var retainedWindow = window;
                window.Closed += (_, _) => this.ReleaseAccessAdministration(retainedWindow);
                window.KeyDown += (_, arguments) =>
                {
                    if (arguments.Key != Key.Escape) { return; }
                    retainedWindow.Close();
                    this.CredentialAccessKey.Focus();
                    arguments.Handled = true;
                };
            }
            else
            {
                if (!activate && page == CredentialAccessPage.Requests) { window.PresentRequestsWithoutActivation(); }
                else { window.SelectPage(page); }
            }
            if (!window.IsVisible)
            {
                window.ShowActivated = activate;
                window.Show(this);
                shown = true;
            }
            else if (activate) { window.Activate(); }
            await window.RefreshAsync();
            return shown && !this.accessAdministrationShuttingDown;
        }
        catch (OperationCanceledException) when (window?.IsDisposed == true || this.accessAdministrationShuttingDown)
        {
            // Closing a successfully shown surface acknowledges presentation, even
            // when its owned administration refresh is still being cancelled.
            return shown && !this.accessAdministrationShuttingDown;
        }
        catch (Exception)
        {
            this.ReportAccessAdministrationFailure();
            if (window is not null)
            {
                window.Close();
                this.ReleaseAccessAdministration(window);
            }
        }
        return false;
    }

    private void ReleaseAccessAdministration(AccessAdministrationWindow window)
    {
        if (!ReferenceEquals(this.LastAccessAdministrationWindow, window)) { return; }
        this.LastAccessAdministrationWindow = null;
        this.accessAdministrationDisposals.Add(this.DisposeAdministrationWindowAsync(window));
    }

    private async Task DisposeAdministrationWindowAsync(AccessAdministrationWindow window)
    {
        try { await window.DisposeAsync(); }
        catch (Exception)
        {
            // Report only a fixed safe message, never backend/provider exception content.
            this.ReportAccessAdministrationFailure();
        }
    }

    private void ReportAccessAdministrationFailure()
    {
        if (this.DataContext is MainWindowViewModel viewModel)
        {
            viewModel.ReportAccessAdministrationFailure();
        }
    }

    internal Task DisposeClosedAccessAdministrationAsync() =>
        Task.WhenAll(this.accessAdministrationDisposals.ToArray());

    internal async ValueTask DisposeAccessAdministrationAsync()
    {
        this.accessAdministrationShuttingDown = true;
        this.DetachAuthorizationPresentation();
        this.LastAccessAdministrationWindow?.Close();
        await this.AccessAdministrationReady;
        await this.DisposeClosedAccessAdministrationAsync();
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
