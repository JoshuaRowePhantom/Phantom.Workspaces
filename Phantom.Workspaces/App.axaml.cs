using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var repositorySource = RepositorySource.Parse(Program.StartupArguments);
            InitializeRepositoryIfNeeded(repositorySource);
            var viewModel = new MainWindowViewModel(repositorySource);
            desktop.MainWindow = new MainWindow(viewModel);
            _ = viewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void InitializeRepositoryIfNeeded(
        RepositorySource repositorySource)
    {
        if (repositorySource.SourceType != RepositorySourceType.LocalGit)
        {
            return;
        }

        var gitDataAccessLayer = new GitDataAccessLayer(repositorySource.RawValue);
        gitDataAccessLayer.InitializeLocalRepository();

        var exportResult = gitDataAccessLayer.ExportAsync(new ExportRequest()).GetAwaiter().GetResult();
        var hasEntities = exportResult.ChangeBatches
            .SelectMany(static batch => batch.Entities)
            .Any(static snapshot => snapshot.Data is not null);
        if (hasEntities)
        {
            return;
        }

        var errors = new SchemaPopulator(gitDataAccessLayer).Populate().GetAwaiter().GetResult();
        if (errors.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Failed to initialize repository schemas: {string.Join(" | ", errors.Select(static error => error.Message))}");
    }
}