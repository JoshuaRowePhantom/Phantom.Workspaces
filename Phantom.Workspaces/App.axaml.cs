using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Templates;
using Phantom.Workspaces.ViewModels;
using Phantom.Workspaces.ViewModels.Configuration;

namespace Phantom.Workspaces;

public partial class App : Application
{
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

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (CommandLineOptions.IsHelpRequested(Program.StartupArguments))
            {
                var helpWindow = new HelpWindow();
                desktop.MainWindow = helpWindow;
                base.OnFrameworkInitializationCompleted();
                helpWindow.Show();
                return;
            }

            var loadingViewModel = new LoadingWindowViewModel();
            var loadingWindow = new LoadingWindow(loadingViewModel);
            desktop.MainWindow = loadingWindow;

            base.OnFrameworkInitializationCompleted();

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
            var viewModel = new MainWindowViewModel(repositorySource, configuration);

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