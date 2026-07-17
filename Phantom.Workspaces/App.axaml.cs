using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.Updates;
using Phantom.Workspaces.Templates;
using Phantom.Workspaces.ViewModels;
using Phantom.Workspaces.ViewModels.Configuration;

namespace Phantom.Workspaces;

public partial class App : Application
{
    private TrayIconController? trayIconController;
    private UpdateController? updateController;
    private bool isExiting;

    /// <summary>
    /// The live update controller for the running, installed application, or <c>null</c> when the
    /// process is not running from an install layout. Shared by the tray icon and the Updates
    /// settings section so both reflect the same state.
    /// </summary>
    public IUpdateController? UpdateController => this.updateController;

    public App()
    {
        // Enables the shared "copyable-text" TextBox style's copy button across this app's windows
        // (mirrors the Agent.Gui app). Clicking the button copies the TextBox's text to the clipboard.
        Button.ClickEvent.AddClassHandler<Button>(OnCopyableTextButtonClick);
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        this.AddWorkspaceDataTemplates();
    }

    private static void OnCopyableTextButtonClick(Button button, RoutedEventArgs eventArgs)
    {
        if (!button.Classes.Contains("copyable-text-button"))
        {
            return;
        }

        var textBox = button.GetVisualAncestors().OfType<TextBox>().FirstOrDefault();
        if (textBox is null)
        {
            return;
        }

        var hasSelection = textBox.SelectionStart != textBox.SelectionEnd;
        if (!hasSelection)
        {
            textBox.SelectAll();
        }

        textBox.Copy();

        if (!hasSelection)
        {
            textBox.ClearSelection();
        }

        eventArgs.Handled = true;
    }

    private void AddWorkspaceDataTemplates()
    {
        foreach (var template in new WorkspaceDataTemplates())
        {
            this.DataTemplates.Add(template);
        }
    }

    /// <summary>
    /// Subscribes the primary instance's activation signal (raised when a duplicate launch for the
    /// same configuration file occurs) to restoring and foregrounding the current main window. The
    /// signal arrives on a background listener thread, so the restore is marshalled to the UI thread.
    /// </summary>
    private static void WireSingleInstanceActivation(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (Program.InstanceGuard is not { } guard)
        {
            return;
        }

        guard.ActivationRequested += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (desktop.MainWindow is not { } window)
            {
                return;
            }

            if (!window.IsVisible)
            {
                window.Show();
            }

            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Activate();
        });

        guard.StartActivationListener();
    }

    /// <summary>
    /// Builds the live update controller (when running from an install layout), shows the tray icon
    /// wired to it, starts the periodic background update check, and installs close-to-tray behaviour
    /// on the main window. When the process is not installed (e.g. a development run) this is a no-op
    /// beyond leaving the window to close normally.
    /// </summary>
    private void WireTrayAndUpdates(
        IClassicDesktopStyleApplicationLifetime desktop,
        Window mainWindow,
        WorkspacesConfiguration configuration)
    {
        void RequestShutdown() => Dispatcher.UIThread.Post(() =>
        {
            this.isExiting = true;
            desktop.Shutdown();
        });

        this.updateController = UpdateControllerFactory.TryCreate(configuration, RequestShutdown);
        if (this.updateController is null)
        {
            return;
        }

        this.trayIconController = new TrayIconController(
            this.updateController,
            openWindow: () => RestoreMainWindow(mainWindow),
            openSettings: () => _ = OpenSettingsFromTrayAsync(mainWindow),
            exit: RequestShutdown,
            dispatch: action => Dispatcher.UIThread.Post(action));

        this.updateController.StartPeriodicChecks();
        this.InstallCloseToTray(mainWindow, configuration);

        desktop.Exit += (_, _) =>
        {
            this.trayIconController?.Dispose();
            this.updateController?.Dispose();
        };
    }

    private void InstallCloseToTray(Window mainWindow, WorkspacesConfiguration configuration)
    {
        mainWindow.Closing += (_, eventArgs) =>
        {
            if (this.isExiting || !configuration.Update.CloseToTray)
            {
                return;
            }

            eventArgs.Cancel = true;
            mainWindow.Hide();
        };
    }

    private static void RestoreMainWindow(Window window)
    {
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    private static async System.Threading.Tasks.Task OpenSettingsFromTrayAsync(Window mainWindow)
    {
        RestoreMainWindow(mainWindow);
        if (mainWindow.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var persistenceService = new ConfigurationPersistenceService();
        var configuration = persistenceService.ConfigurationExists()
            ? await persistenceService.LoadAsync()
            : new WorkspacesConfiguration();

        var settingsViewModel = new WorkspacesSettingsViewModel(
            persistenceService,
            configuration,
            viewModel,
            (Current as App)?.UpdateController,
            action => Dispatcher.UIThread.Post(action));
        var settingsWindow = new SettingsDialogWindow(settingsViewModel);
        await settingsWindow.ShowDialog(mainWindow);
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (CommandLineOptions.IsHelpRequested(Program.StartupArguments))
            {
                var helpWindow = new HelpWindow();
                desktop.MainWindow = helpWindow;
                base.OnFrameworkInitializationCompleted();
                UnhandledExceptionHandler.InstallDispatcherHandler();
                helpWindow.Show();
                return;
            }

            var loadingViewModel = new LoadingWindowViewModel();
            var loadingWindow = new LoadingWindow(loadingViewModel);
            desktop.MainWindow = loadingWindow;

            base.OnFrameworkInitializationCompleted();
            UnhandledExceptionHandler.InstallDispatcherHandler();

            WireSingleInstanceActivation(desktop);

            loadingViewModel.StatusText = "Reading startup configuration.";
            var persistenceService = CommandLineOptions.TryGetConfigurationFilePath(Program.StartupArguments, out var configurationFilePath)
                ? new ConfigurationPersistenceService(configurationFilePath!)
                : new ConfigurationPersistenceService();
            
            var configuration = persistenceService.ConfigurationExists()
                ? await persistenceService.LoadAsync()
                : null;
            
            var repositorySource = configuration is not null
                ? configuration.ToRepositorySource()
                : await ResolveStartupRepositorySourceAsync(desktop, loadingWindow, persistenceService);
                
            if (repositorySource is null)
            {
                desktop.Shutdown();
                return;
            }

            loadingWindow.Show();
            loadingViewModel.StatusText = "Initializing main workspace view model.";

            // #1086: resolve the log directory in exactly one place (driven by WorkspacesConfiguration)
            // and build the process logger factory backed by the rolling file provider before any
            // service that logs is constructed. The same directory is later handed to the embedded
            // web host so GUI and web host never diverge.
            var logDirectoryProvider = new Services.Logging.LogDirectoryProvider(
                configuration ?? new WorkspacesConfiguration(),
                configurationFilePath);
            var loggerFactory = Services.Logging.LoggingBootstrap.CreateLoggerFactory(logDirectoryProvider);

            var agentPersistenceStoreCache = new AgentPersistenceStoreCache();
            var agentPersistenceStore = await agentPersistenceStoreCache.GetOrCreateAsync(repositorySource);
            var foregroundScheduler = SynchronizationContextTaskScheduler.FromCurrent();
            var agentChatFactory = new AgentChatFactory(agentPersistenceStore, new AgentServices(), foregroundScheduler);
            var applicationServices = new ApplicationServices(
                new RunningAgentChatTable(agentChatFactory),
                agentPersistenceStoreCache,
                loggerFactory: loggerFactory,
                logDirectoryProvider: logDirectoryProvider);
            var viewModel = new MainWindowViewModel(repositorySource, configuration, applicationServices: applicationServices);

            loadingViewModel.StatusText = "Loading repository data and profile.";
            try
            {
                await viewModel.InitializeAsync();
            }
            catch (Exception ex)
            {
                loadingViewModel.StatusText = $"Failed to connect: {ex.Message}";
                await Task.Delay(5000); // Give user time to read the error
                desktop.Shutdown();
                return;
            }

            loadingViewModel.StatusText = "Opening main window.";
            var mainWindow = new MainWindow(viewModel);
            viewModel.WireWindowFocus(() => RestoreMainWindow(mainWindow));
            mainWindow.Icon = TrayIconImageFactory.Create(updateAvailable: false);
            desktop.MainWindow = mainWindow;
            mainWindow.Show();
            loadingWindow.Close();

            this.WireTrayAndUpdates(desktop, mainWindow, configuration ?? new WorkspacesConfiguration());
            return;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Resolves the repository source for startup. Explicit startup arguments take precedence;
    /// otherwise the persisted configuration is used, running the first-run installation wizard
    /// when no configuration exists yet. Returns <see langword="null"/> when the user cancels the
    /// wizard (signalling shutdown).
    /// </summary>
    private static async System.Threading.Tasks.Task<RepositorySource?> ResolveStartupRepositorySourceAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        Window loadingWindow,
        ConfigurationPersistenceService persistenceService)
    {
        if (persistenceService.ConfigurationExists())
        {
            var configuration = await persistenceService.LoadAsync();
            return configuration.ToRepositorySource();
        }

        var wizardViewModel = new WorkspacesSettingsViewModel(persistenceService);
        var wizardWindow = new InstallationWizardWindow(wizardViewModel);
        desktop.MainWindow = wizardWindow;

        var wizardClosed = new System.Threading.Tasks.TaskCompletionSource();
        wizardWindow.Closed += (_, _) => wizardClosed.TrySetResult();
        loadingWindow.Hide();
        wizardWindow.Show();
        await wizardClosed.Task;

        if (!wizardWindow.Completed || wizardWindow.Result is null)
        {
            return null;
        }

        desktop.MainWindow = loadingWindow;
        return wizardWindow.Result.ToRepositorySource();
    }
}