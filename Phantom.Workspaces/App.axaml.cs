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
            var repositorySource = RepositorySource.Parse(Program.StartupArguments);
            var viewModel = new MainWindowViewModel(repositorySource);
            desktop.MainWindow = new MainWindow(viewModel);
            await viewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}