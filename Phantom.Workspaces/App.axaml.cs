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
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.Secrets;
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
            action => Dispatcher.UIThread.Post(action),
            viewModel.LogDirectoryProvider,
            new Phantom.Workspaces.Install.RealProcessLauncher());
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

            // #1373: install the process-wide ambient docker logger factory so the production
            // MongoDbConnectionBroker default path (used by the persistence/edit-store factories)
            // logs docker stdout/stderr through the real GUI host logger instead of discarding it.
            Containers.DockerCommandRunnerLogging.LoggerFactory = loggerFactory;

            // #1093: route global uncaught/unobserved exceptions through the #1086 file facility now
            // that the logger factory exists (the crash-dialog handlers installed in Program.Main stay
            // in place; this adds the missing logging half).
            Services.Logging.GlobalExceptionLogging.Register(loggerFactory);

            var agentPersistenceStoreCache = new AgentPersistenceStoreCache();
            var agentPersistenceStore = await agentPersistenceStoreCache.GetOrCreateAsync(repositorySource);
            var foregroundScheduler = SynchronizationContextTaskScheduler.FromCurrent();

            IPlatformSecretStore platformStore;
            if (OperatingSystem.IsWindows())
            {
                platformStore = new WindowsCredentialManagerSecretStore();
            }
            else
            {
                platformStore = new NullPlatformSecretStore();
            }

            var allowedSecretsStore = new AllowedSecretsStore(new AllowedSecretsStoreConfiguration());
            var hwndProvider = new AvaloniaHwndProvider();
            ICredentialPicker credentialPicker;
            if (OperatingSystem.IsWindows())
            {
                credentialPicker = new WindowsCredentialPicker(hwndProvider);
            }
            else
            {
                credentialPicker = new NullCredentialPicker();
            }

            var dialogHost = new AvaloniaSecretUseDialogHost(credentialPicker);
            var secretProvider = new SecretProvider(allowedSecretsStore, platformStore, dialogHost);

            // #1385: register the interactive MCP OAuth redirect handler (system browser + loopback
            // listener, consent-gated) into the #1382 McpOAuthOptions.RedirectDelegateProvider seam so
            // the MCP transport factory drives real interactive OAuth in the GUI host. Headless hosts
            // (CLI / Web.Server / tests) do not wire this and keep the failing default.
            var mcpOAuthOptions = Services.Mcp.McpOAuthComposition.CreateOptions(secretProvider);
            var agentChatFactory = new AgentChatFactory(
                agentPersistenceStore,
                new AgentServices { SecretProvider = secretProvider, McpOAuthOptions = mcpOAuthOptions },
                foregroundScheduler);
            var applicationServices = new ApplicationServices(
                new RunningAgentChatTable(agentChatFactory),
                agentPersistenceStoreCache,
                loggerFactory: loggerFactory,
                logDirectoryProvider: logDirectoryProvider,
                configurationPersistence: persistenceService,
                secretProvider: secretProvider,
                credentialPicker: credentialPicker,
                allowedSecretsStore: allowedSecretsStore,
                platformSecretStore: platformStore);
            var viewModel = new MainWindowViewModel(repositorySource, configuration, applicationServices: applicationServices);

            // #1172: register the canonical URL opener now that MainWindowViewModel exists
            // (it implements IWorkspaceTabService). The TopLevel accessor is late-bound to
            // desktop.MainWindow because the real MainWindow is created after InitializeAsync.
            applicationServices.SetUrlOpener(
                Services.UrlOpener.CreateDefault(
                    viewModel,
                    () => desktop.MainWindow as Avalonia.Controls.TopLevel));

            loadingViewModel.StatusText = "Loading repository data and profile.";

            // #1186: Route the "initialize + open main window" sequence through
            // StartupSplashRunner so the loading window is dismissed inside a
            // finally block regardless of how initialize exits. Previously the
            // splash was closed only on the happy path, so a fault or hang inside
            // RestoreSubAgentsAsync (the reported #1186 cause) left it stuck in
            // front indefinitely.
            var succeeded = await StartupSplashRunner.RunWithSplashDismissAsync(
                loggerFactory: loggerFactory,
                initializeAsync: () => viewModel.InitializeAsync(),
                setStatus: msg => loadingViewModel.StatusText = msg,
                onFaultDelay: () => Task.Delay(5000), // Give user time to read the error
                shutdown: () => desktop.Shutdown(),
                postInitialize: () =>
                {
                    loadingViewModel.StatusText = "Opening main window.";
                    var mainWindow = new MainWindow(viewModel);
                    viewModel.WireWindowFocus(() => RestoreMainWindow(mainWindow));
                    mainWindow.Icon = TrayIconImageFactory.Create(updateAvailable: false);
                    desktop.MainWindow = mainWindow;
                    mainWindow.Show();
                    this.WireTrayAndUpdates(desktop, mainWindow, configuration ?? new WorkspacesConfiguration());
                },
                closeSplash: () => loadingWindow.Close());

            _ = succeeded;
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
