using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Phantom.Workspaces.Templates;
using Phantom.Workspaces.ViewModels;

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
            var repositorySource = RepositorySource.Parse(Program.StartupArguments);

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
}