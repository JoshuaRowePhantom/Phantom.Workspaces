using System.IO;
using System.Threading.Tasks;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.ViewModels.Configuration;

namespace Phantom.Workspaces.Tests;

public sealed class WorkspacesSettingsViewModelTests
{
    [AvaloniaFact]
    public void Repository_ActiveSettings_ResolvesBySubtype()
    {
        var viewModel = new RepositoryConnectionSettingsViewModel
        {
            Mode = DataAccessMode.LocalMongoContainer,
        };
        Assert.IsType<LocalMongoContainerSettingsViewModel>(viewModel.ActiveSettings);

        viewModel.Mode = DataAccessMode.Web;
        Assert.IsType<WebSettingsViewModel>(viewModel.ActiveSettings);

        viewModel.Mode = DataAccessMode.RemoteMongo;
        Assert.IsType<RemoteMongoSettingsViewModel>(viewModel.ActiveSettings);

        viewModel.Mode = DataAccessMode.DevTunnelWeb;
        Assert.IsType<DevTunnelWebSettingsViewModel>(viewModel.ActiveSettings);
    }

    [AvaloniaFact]
    public void Repository_IsValid_DelegatesToActiveSubtype()
    {
        var viewModel = new RepositoryConnectionSettingsViewModel { Mode = DataAccessMode.Web };
        Assert.False(viewModel.IsValid);

        viewModel.Web.Endpoint = "https://workspaces.example/";
        Assert.True(viewModel.IsValid);

        // Switching to an unconfigured mode flips validity.
        viewModel.Mode = DataAccessMode.LocalMongoContainer;
        Assert.False(viewModel.IsValid);

        viewModel.LocalMongoContainer.ContainerName = "mongodb";
        viewModel.LocalMongoContainer.RootCollectionName = "entities";
        Assert.True(viewModel.IsValid);
    }

    [AvaloniaFact]
    public void Repository_ToProfile_UsesActiveSubtype()
    {
        var viewModel = new RepositoryConnectionSettingsViewModel { Mode = DataAccessMode.DevTunnelWeb };
        viewModel.DevTunnelWeb.Endpoint = "https://host.devtunnels.ms/";
        viewModel.DevTunnelWeb.AccessTokenSource = "DEVTUNNEL_TOKEN";

        var profile = viewModel.ToProfile();

        Assert.Equal(DataAccessMode.DevTunnelWeb, profile.Mode);
        Assert.Equal("https://host.devtunnels.ms/", profile.WebEndpoint);
        Assert.Equal("DEVTUNNEL_TOKEN", profile.DevTunnelTokenSource);
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
    public void Settings_CanSave_TracksSectionValidity()
    {
        var service = new ConfigurationPersistenceService(CreateTempConfigPath());
        var settings = new WorkspacesSettingsViewModel(service);

        var raised = false;
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkspacesSettingsViewModel.CanSave))
            {
                raised = true;
            }
        };

        settings.Repository.Mode = DataAccessMode.Web;
        Assert.False(settings.CanSave);

        settings.Repository.Web.Endpoint = "https://workspaces.example/";
        Assert.True(settings.CanSave);
        Assert.True(raised);
    }

    [AvaloniaFact]
    public async Task Settings_SaveAsync_PersistsConfiguration_UsedByWizardAndDialog()
    {
        var path = CreateTempConfigPath();
        var service = new ConfigurationPersistenceService(path);

        // The same view model type backs both the wizard and the settings dialog.
        var settings = new WorkspacesSettingsViewModel(service);
        settings.Repository.Mode = DataAccessMode.Web;
        settings.Repository.Web.Endpoint = "https://workspaces.example/";
        settings.Theme = "FluentDark";

        try
        {
            var saved = await settings.SaveAsync();

            Assert.Equal(DataAccessMode.Web, saved.DataAccess.Mode);
            Assert.Equal("FluentDark", saved.Visual.Theme);

            var reloaded = await service.LoadAsync(path);
            Assert.Equal("https://workspaces.example/", reloaded.DataAccess.WebEndpoint);
            Assert.Equal("FluentDark", reloaded.Visual.Theme);
        }
        finally
        {
            DeleteTempConfig(path);
        }
    }

    [AvaloniaFact]
    public void Windows_BindSharedViewModel_AndConstruct()
    {
        var service = new ConfigurationPersistenceService(CreateTempConfigPath());
        var settings = new WorkspacesSettingsViewModel(service);

        var wizard = new InstallationWizardWindow(settings);
        var dialog = new SettingsDialogWindow(settings);

        Assert.Same(settings, wizard.DataContext);
        Assert.Same(settings, dialog.DataContext);
    }

    [AvaloniaFact]
    public void Settings_Sections_ExposeRepositoryRemoteAccessAndAppearance()
    {
        var service = new ConfigurationPersistenceService(CreateTempConfigPath());
        var settings = new WorkspacesSettingsViewModel(service);

        Assert.Collection(
            settings.Sections,
            section =>
            {
                Assert.Equal("Repository", section.Title);
                Assert.Same(settings.Repository, section.Content);
            },
            section =>
            {
                Assert.Equal("Remote access", section.Title);
                Assert.Same(settings.RemoteAccess, section.Content);
            },
            section =>
            {
                Assert.Equal("Appearance", section.Title);
                Assert.Same(settings.Appearance, section.Content);
            });

        // The master-detail layout starts on the first section.
        Assert.Same(settings.Sections[0], settings.SelectedSection);
    }

    [AvaloniaFact]
    public void Settings_SelectedSection_IgnoresNullSelection()
    {
        var service = new ConfigurationPersistenceService(CreateTempConfigPath());
        var settings = new WorkspacesSettingsViewModel(service);

        settings.SelectedSection = settings.Sections[2];
        Assert.Equal("Appearance", settings.SelectedSection.Title);

        // ListBox clears SelectedItem to null transiently; keep the last real selection.
        settings.SelectedSection = null!;
        Assert.Equal("Appearance", settings.SelectedSection.Title);
    }

    [AvaloniaFact]
    public void Settings_Theme_DelegatesToAppearanceSection()
    {
        var service = new ConfigurationPersistenceService(CreateTempConfigPath());
        var settings = new WorkspacesSettingsViewModel(service);

        settings.Theme = "FluentDark";
        Assert.Equal("FluentDark", settings.Appearance.Theme);

        settings.Appearance.Theme = "FluentLight";
        Assert.Equal("FluentLight", settings.Theme);
        Assert.Equal("FluentLight", settings.BuildConfiguration().Visual.Theme);
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
