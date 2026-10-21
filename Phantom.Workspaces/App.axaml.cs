using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.Access;
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
    private AccessHostServices? accessHost;
    private MainWindowViewModel? runtimeViewModel;
    private MainWindow? runtimeWindow;
    private AuthorizationRequestsPopupViewModel? authorizationPopup;
    private AgentChatFactory? agentChatFactory;
    private AgentPersistenceStoreCache? persistenceStoreCache;
    private ILoggerFactory? processLoggerFactory;
    private Task? shutdownTask;
    private bool shutdownCompleted;

    /// <summary>
    /// The live update controller for the running, installed application, or <c>null</c> when
    /// self-update is unavailable. Shared by the tray icon and the Updates
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
        WorkspacesConfiguration configuration,
        Microsoft.Extensions.Logging.ILoggerFactory loggerFactory,
        ApplicationServices applicationServices)
    {
        void RequestShutdown() => Dispatcher.UIThread.Post(() => this.RequestShutdown(desktop));

        var updateCapability = UpdateControllerFactory.TryCreate(configuration, RequestShutdown, loggerFactory);
        this.updateController = updateCapability.Controller;
        applicationServices.SetUpdateCapability(this.updateController, updateCapability.UnavailableReason);
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

        var settingsViewModel = await viewModel.CreateSettingsDialogViewModelAsync();
        var settingsWindow = new SettingsDialogWindow(settingsViewModel);
        await settingsWindow.ShowDialog(mainWindow);
        if (settingsWindow.Result is { } configuration)
        {
            await viewModel.ApplyRemoteAccessChangeAsync(
                configuration.RemoteHosting,
                configuration.DevTunnel);
        }
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

            var persistenceService = CommandLineOptions.TryGetConfigurationFilePath(Program.StartupArguments, out var configurationFilePath)
                ? new ConfigurationPersistenceService(configurationFilePath!)
                : new ConfigurationPersistenceService();
            var configuration = persistenceService.ConfigurationExists()
                ? await persistenceService.LoadAsync()
                : null;
            IPlatformSecretStore platformStore = OperatingSystem.IsWindows()
                ? new WindowsCredentialManagerSecretStore()
                : new NullPlatformSecretStore();
            this.accessHost = CreateAccessHostBeforeStartup(new AccessHostOptions
            {
                PlatformSecretStore = platformStore,
                Dispatch = action => Dispatcher.UIThread.Post(action),
            });
            try
            {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.ShutdownRequested += (_, arguments) =>
            {
                if (this.shutdownCompleted) { return; }
                arguments.Cancel = true;
                this.RequestShutdown(desktop);
            };
            var loadingViewModel = new LoadingWindowViewModel { AuthorizationRows = this.accessHost.AuthorizationRows };
            var loadingWindow = new LoadingWindow(loadingViewModel);
            var dismissingSplash = false;
            loadingWindow.Closing += (_, arguments) =>
            {
                if (dismissingSplash || this.shutdownCompleted || arguments.Cancel) { return; }
                arguments.Cancel = true;
                this.RequestShutdown(desktop);
            };
            desktop.MainWindow = loadingWindow;

            base.OnFrameworkInitializationCompleted();
            UnhandledExceptionHandler.InstallDispatcherHandler();

            WireSingleInstanceActivation(desktop);

            loadingViewModel.StatusText = "Reading startup configuration.";
            var repositorySource = configuration is not null
                ? configuration.ToRepositorySource()
                : await ResolveStartupRepositorySourceAsync(desktop, loadingWindow, persistenceService);
            if (this.isExiting) { return; }
                
            if (repositorySource is null)
            {
                this.RequestShutdown(desktop);
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
            this.processLoggerFactory = loggerFactory;

            // #1373: install the process-wide ambient docker logger factory so the production
            // MongoDbConnectionBroker default path (used by the persistence/edit-store factories)
            // logs docker stdout/stderr through the real GUI host logger instead of discarding it.
            Containers.DockerCommandRunnerLogging.LoggerFactory = loggerFactory;

            // #1093: route global uncaught/unobserved exceptions through the #1086 file facility now
            // that the logger factory exists (the crash-dialog handlers installed in Program.Main stay
            // in place; this adds the missing logging half).
            Services.Logging.GlobalExceptionLogging.Register(loggerFactory);

            var agentPersistenceStoreCache = new AgentPersistenceStoreCache(
                this.accessHost.Broker, () => this.accessHost.PresentationScope);
            this.persistenceStoreCache = agentPersistenceStoreCache;
            var agentPersistenceStore = await agentPersistenceStoreCache.GetOrCreateAsync(repositorySource);
            if (this.isExiting) { return; }
            var foregroundScheduler = SynchronizationContextTaskScheduler.FromCurrent();

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

            var agentChatFactory = new AgentChatFactory(
                agentPersistenceStore,
                Services.AgentServicesComposition.ComposeHostServices(this.accessHost.Broker, loggerFactory) with
                {
                    AccessBroker = this.accessHost.Broker,
                },
                foregroundScheduler);
            this.agentChatFactory = agentChatFactory;
            var transportFactoryRegistryProvider = new Services.TransportFactoryRegistryProvider();
            var applicationServices = new ApplicationServices(
                new RunningAgentChatTable(
                    agentChatFactory,
                    Services.AgentSessionRuntimeContextFactory.FromProvider(transportFactoryRegistryProvider),
                    loggerFactory.CreateLogger<RunningAgentChatTable>()),
                agentPersistenceStoreCache,
                loggerFactory,
                accessBroker: this.accessHost.Broker,
                accessPresentationScope: () => this.accessHost.PresentationScope,
                logDirectoryProvider: logDirectoryProvider,
                configurationPersistence: persistenceService,
                secretProvider: secretProvider,
                credentialPicker: credentialPicker,
                allowedSecretsStore: allowedSecretsStore,
                platformSecretStore: platformStore,
                transportFactoryRegistryProvider: transportFactoryRegistryProvider);
            var viewModel = new MainWindowViewModel(repositorySource, configuration, applicationServices: applicationServices);
            this.runtimeViewModel = viewModel;

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
                shutdown: () => this.RequestShutdown(desktop),
                postInitialize: () =>
                {
                    if (this.isExiting) { return; }
                    loadingViewModel.StatusText = "Opening main window.";
                    var mainWindow = new MainWindow(viewModel);
                    this.runtimeWindow = mainWindow;
                    this.authorizationPopup = new AuthorizationRequestsPopupViewModel
                    {
                        Rows = this.accessHost.AuthorizationRows,
                    };
                    mainWindow.AttachAccessHost(this.accessHost, this.authorizationPopup);
                    viewModel.WireWindowFocus(() => RestoreMainWindow(mainWindow));
                    mainWindow.Icon = TrayIconImageFactory.Create(updateAvailable: false);
                    desktop.MainWindow = mainWindow;
                    mainWindow.Show();
                    this.WireTrayAndUpdates(desktop, mainWindow, configuration ?? new WorkspacesConfiguration(), loggerFactory, applicationServices);
                    mainWindow.Closing += (_, arguments) =>
                    {
                        if (this.shutdownCompleted || arguments.Cancel) { return; }
                        arguments.Cancel = true;
                        this.RequestShutdown(desktop);
                    };
                },
                closeSplash: () =>
                {
                    dismissingSplash = true;
                    loadingWindow.Close();
                });

            _ = succeeded;
            return;
            }
            catch (Exception startupFailure)
            {
                try
                {
                    await (this.shutdownTask ??= this.ShutdownCoreAsync());
                }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException("Startup and cleanup did not complete cleanly.", startupFailure, cleanupFailure);
                }
                finally
                {
                    this.shutdownCompleted = true;
                    desktop.Shutdown();
                }

                throw;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    internal static AccessHostServices CreateAccessHostBeforeStartup(AccessHostOptions options) =>
        AccessHostServices.Create(options);

    private async void RequestShutdown(IClassicDesktopStyleApplicationLifetime desktop)
    {
        this.isExiting = true;
        try
        {
            await (this.shutdownTask ??= this.ShutdownCoreAsync());
        }
        catch (AggregateException failure)
        {
            Console.Error.WriteLine(
                "Application cleanup failed in {0} components.", failure.Flatten().InnerExceptions.Count);
        }
        finally
        {
            this.shutdownCompleted = true;
            desktop.Shutdown();
        }
    }

    private async Task ShutdownCoreAsync()
    {
        var failures = new System.Collections.Generic.List<Exception>();
        async Task CleanAsync(Func<ValueTask> cleanup)
        {
            try { await cleanup(); }
            catch (Exception exception) { failures.Add(exception); }
        }

        if (this.runtimeWindow?.LastAccessAdministrationWindow is { } administration)
        {
            await CleanAsync(administration.DisposeAsync);
        }
        if (this.authorizationPopup is not null) { await CleanAsync(this.authorizationPopup.DisposeAsync); }
        if (this.accessHost is not null) { await CleanAsync(this.accessHost.DisposeAsync); }
        if (this.runtimeViewModel is not null) { await CleanAsync(this.runtimeViewModel.DisposeAsync); }
        if (this.agentChatFactory is not null) { await CleanAsync(this.agentChatFactory.DisposeAsync); }
        if (this.persistenceStoreCache is not null) { await CleanAsync(this.persistenceStoreCache.DisposeAsync); }
        await CleanAsync(() => { this.trayIconController?.Dispose(); return ValueTask.CompletedTask; });
        await CleanAsync(() => { this.updateController?.Dispose(); return ValueTask.CompletedTask; });
        await CleanAsync(() => { this.processLoggerFactory?.Dispose(); return ValueTask.CompletedTask; });
        if (failures.Count > 0)
        {
            throw new AggregateException("Application shutdown did not complete cleanly.", failures);
        }
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
