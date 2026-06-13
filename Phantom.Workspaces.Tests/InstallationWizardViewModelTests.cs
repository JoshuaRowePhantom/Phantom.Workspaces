using System.IO;
using System.Threading.Tasks;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.ViewModels.Configuration;

namespace Phantom.Workspaces.Tests;

public sealed class InstallationWizardViewModelTests
{
    [AvaloniaFact]
    public void Repository_WebMode_RequiresAbsoluteEndpoint()
    {
        var viewModel = new RepositoryConnectionSettingsViewModel
        {
            Mode = DataAccessMode.Web,
        };

        Assert.False(viewModel.IsValid);

        viewModel.WebEndpoint = "https://workspaces.example/";
        Assert.True(viewModel.IsValid);

        var profile = viewModel.ToProfile();
        Assert.Equal(DataAccessMode.Web, profile.Mode);
        Assert.Equal("https://workspaces.example/", profile.WebEndpoint);
    }

    [AvaloniaFact]
    public void Repository_LocalMongo_RequiresContainerAndCollection()
    {
        var viewModel = new RepositoryConnectionSettingsViewModel
        {
            Mode = DataAccessMode.LocalMongoContainer,
            MongoContainerName = "mongodb",
        };

        Assert.False(viewModel.IsValid);

        viewModel.MongoRootCollectionName = "entities";
        Assert.True(viewModel.IsValid);
    }

    [AvaloniaFact]
    public void RemoteAccess_TokenMode_RequiresTokenSource()
    {
        var viewModel = new RemoteAccessSettingsViewModel
        {
            DevTunnelAccessMode = DevTunnelAccessMode.Token,
        };

        Assert.False(viewModel.IsValid);

        viewModel.DevTunnelAccessTokenSource = "DEVTUNNEL_TOKEN";
        Assert.True(viewModel.IsValid);
    }

    [AvaloniaFact]
    public void RemoteAccess_AnonymousMode_ShowsWarning()
    {
        var viewModel = new RemoteAccessSettingsViewModel
        {
            DevTunnelAccessMode = DevTunnelAccessMode.Anonymous,
        };

        Assert.True(viewModel.IsAnonymousAccessWarningVisible);
    }

    [AvaloniaFact]
    public void Wizard_CanComplete_TracksSectionValidity()
    {
        var service = new ConfigurationPersistenceService(CreateTempConfigPath());
        var wizard = new InstallationWizardViewModel(service);

        var raised = false;
        wizard.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InstallationWizardViewModel.CanComplete))
            {
                raised = true;
            }
        };

        wizard.Repository.Mode = DataAccessMode.Web;
        Assert.False(wizard.CanComplete);

        wizard.Repository.WebEndpoint = "https://workspaces.example/";
        Assert.True(wizard.CanComplete);
        Assert.True(raised);
    }

    [AvaloniaFact]
    public async Task Wizard_CompleteAsync_PersistsConfiguration()
    {
        var path = CreateTempConfigPath();
        var service = new ConfigurationPersistenceService(path);
        var wizard = new InstallationWizardViewModel(service);
        wizard.Repository.Mode = DataAccessMode.Web;
        wizard.Repository.WebEndpoint = "https://workspaces.example/";

        try
        {
            var saved = await wizard.CompleteAsync();

            Assert.Equal(DataAccessMode.Web, saved.DataAccess.Mode);
            Assert.True(service.ConfigurationExists(path));

            var reloaded = await service.LoadAsync(path);
            Assert.Equal("https://workspaces.example/", reloaded.DataAccess.WebEndpoint);
        }
        finally
        {
            DeleteTempConfig(path);
        }
    }

    [AvaloniaFact]
    public async Task SettingsDialog_SaveAsync_PersistsUpdatedTheme()
    {
        var path = CreateTempConfigPath();
        var service = new ConfigurationPersistenceService(path);
        var configuration = new WorkspacesConfiguration
        {
            DataAccess = new DataAccessConnectionProfile
            {
                Mode = DataAccessMode.Web,
                WebEndpoint = "https://workspaces.example/",
            },
        };
        var dialog = new SettingsDialogViewModel(service, configuration)
        {
            Theme = "FluentDark",
        };

        try
        {
            var saved = await dialog.SaveAsync();

            Assert.Equal("FluentDark", saved.Visual.Theme);

            var reloaded = await service.LoadAsync(path);
            Assert.Equal("FluentDark", reloaded.Visual.Theme);
        }
        finally
        {
            DeleteTempConfig(path);
        }
    }

    private static string CreateTempConfigPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"phantom-wizard-{System.Guid.NewGuid():N}");
        return Path.Combine(directory, "config.json");
    }

    private static void DeleteTempConfig(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
