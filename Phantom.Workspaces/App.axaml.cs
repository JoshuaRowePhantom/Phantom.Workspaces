using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Templates;
using Phantom.Workspaces.ViewModels;
using Phantom.Workspaces.ViewModels.Configuration;

namespace Phantom.Workspaces;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        this.AddWorkspaceDataTemplates();
    }

    private void AddWorkspaceDataTemplates()
    {
        foreach (var template in new WorkspaceDataTemplates())
        {
            this.DataTemplates.Add(template);
        }
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var loadingViewModel = new LoadingWindowViewModel();
            var loadingWindow = new LoadingWindow(loadingViewModel);
            desktop.MainWindow = loadingWindow;

            base.OnFrameworkInitializationCompleted();

            loadingViewModel.StatusText = "Reading startup configuration.";
            var repositorySource = await ResolveStartupRepositorySourceAsync(desktop, loadingWindow);
            if (repositorySource is null)
            {
                desktop.Shutdown();
                return;
            }

            loadingWindow.Show();
            loadingViewModel.StatusText = "Initializing main workspace view model.";
            var viewModel = new MainWindowViewModel(repositorySource);

            loadingViewModel.StatusText = "Loading repository data and profile.";
            await viewModel.InitializeAsync();

            loadingViewModel.StatusText = "Opening main window.";
            var mainWindow = new MainWindow(viewModel);
            desktop.MainWindow = mainWindow;
            mainWindow.Show();
            loadingWindow.Close();
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
        Window loadingWindow)
    {
        var argumentSource = RepositorySource.Parse(Program.StartupArguments);
        if (argumentSource is not UnknownRepositorySource)
        {
            return argumentSource;
        }

        var persistenceService = new ConfigurationPersistenceService();
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