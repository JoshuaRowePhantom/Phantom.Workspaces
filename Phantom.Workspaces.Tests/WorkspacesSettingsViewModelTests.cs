using Avalonia.Headless.XUnit;
using System.IO;
using System.Threading.Tasks;
using Moq;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Install;
using Phantom.Workspaces.Services.Logging;
using Phantom.Workspaces.ViewModels;
using Phantom.Workspaces.ViewModels.Configuration;

using Phantom.Workspaces.Testing.Gui;

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

        // LocalMongoContainer pre-fills its required fields with defaults, so it is valid immediately.
        viewModel.Mode = DataAccessMode.LocalMongoContainer;
        Assert.True(viewModel.IsValid);

        // Clearing a required field flips validity.
        viewModel.LocalMongoContainer.RootCollectionName = string.Empty;
        Assert.False(viewModel.IsValid);

        viewModel.LocalMongoContainer.RootCollectionName = "entities";
        Assert.True(viewModel.IsValid);
    }

    [AvaloniaFact]
    public void LocalMongoContainer_DataDirectory_DefaultsToWizardDefault_WhenProfileHasNone()
    {
        var settings = new LocalMongoContainerSettingsViewModel(new DataAccessConnectionProfile());

        Assert.Equal(LocalMongoContainerSettingsViewModel.GetDefaultDataDirectory(), settings.DataDirectory);
        Assert.False(string.IsNullOrWhiteSpace(settings.DataDirectory));
    }

    [AvaloniaFact]
    public void LocalMongoContainer_DataDirectory_PreservesConfiguredValue()
    {
        var settings = new LocalMongoContainerSettingsViewModel(
            new DataAccessConnectionProfile { MongoDataDirectory = "D:/explicit/mongo" });

        Assert.Equal("D:/explicit/mongo", settings.DataDirectory);
    }

    [AvaloniaFact]
    public void LocalMongoContainer_FreshProfile_PreFillsDefaultsAndIsValid()
    {
        var settings = new LocalMongoContainerSettingsViewModel(new DataAccessConnectionProfile());

        Assert.Equal(LocalMongoContainerSettingsViewModel.DefaultContainerName, settings.ContainerName);
        Assert.Equal(LocalMongoContainerSettingsViewModel.DefaultRootCollectionName, settings.RootCollectionName);
        Assert.Equal(LocalMongoContainerSettingsViewModel.DefaultDatabaseName, settings.DatabaseName);
        Assert.False(string.IsNullOrWhiteSpace(settings.DataDirectory));

        // With every field pre-filled, the wizard is valid out of the box and Complete setup is enabled.
        Assert.True(settings.IsValid);
    }

    [AvaloniaFact]
    public void LocalMongoContainer_PreservesConfiguredNames()
    {
        var settings = new LocalMongoContainerSettingsViewModel(new DataAccessConnectionProfile
        {
            MongoContainerName = "custom-container",
            MongoRootCollectionName = "custom-root",
            MongoDatabaseName = "custom-db",
        });

        Assert.Equal("custom-container", settings.ContainerName);
        Assert.Equal("custom-root", settings.RootCollectionName);
        Assert.Equal("custom-db", settings.DatabaseName);
    }

    [AvaloniaFact]
    public void LocalMongoContainer_IsValid_RequiresDataDirectory()
    {
        var settings = new LocalMongoContainerSettingsViewModel(new DataAccessConnectionProfile())
        {
            ContainerName = "mongodb",
            RootCollectionName = "entities",
        };

        // The wizard/GUI default makes it valid out of the box.
        Assert.True(settings.IsValid);

        // Clearing the data directory invalidates it: the data layer requires it configured.
        settings.DataDirectory = string.Empty;
        Assert.False(settings.IsValid);

        settings.DataDirectory = "C:/mongo-data";
        Assert.True(settings.IsValid);
    }

    [AvaloniaFact]
    public void Repository_ToProfile_UsesActiveSubtype()
    {
        var viewModel = new RepositoryConnectionSettingsViewModel { Mode = DataAccessMode.DevTunnelWeb };
        viewModel.DevTunnelWeb.Endpoint = "https://host.devtunnels.ms/";

        var profile = viewModel.ToProfile();

        Assert.Equal(DataAccessMode.DevTunnelWeb, profile.Mode);
        Assert.Equal("https://host.devtunnels.ms/", profile.WebEndpoint);
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

        try
        {
            var saved = await settings.SaveAsync();

            Assert.Equal(DataAccessMode.Web, saved.DataAccess.Mode);

            var reloaded = await service.LoadAsync(path);
            Assert.Equal("https://workspaces.example/", reloaded.DataAccess.WebEndpoint);
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
    public void Settings_Sections_ExposeRepositoryAndRemoteAccess()
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
            });

        // The master-detail layout starts on the first section.
        Assert.Same(settings.Sections[0], settings.SelectedSection);
    }

    [AvaloniaFact]
    public void Settings_SelectedSection_IgnoresNullSelection()
    {
        var service = new ConfigurationPersistenceService(CreateTempConfigPath());
        var settings = new WorkspacesSettingsViewModel(service);

        settings.SelectedSection = settings.Sections[1];
        Assert.Equal("Remote access", settings.SelectedSection.Title);

        // ListBox clears SelectedItem to null transiently; keep the last real selection.
        settings.SelectedSection = null!;
        Assert.Equal("Remote access", settings.SelectedSection.Title);
    }

    [AvaloniaFact]
    public void BuildConfiguration_ProjectsUserComputerProfileOverride()
    {
        var service = new ConfigurationPersistenceService(CreateTempConfigPath());
        var settings = new WorkspacesSettingsViewModel(service);

        settings.RemoteAccess.UserComputerProfileOverride = "second-instance";
        Assert.Equal("second-instance", settings.BuildConfiguration().UserComputerProfileOverride);

        // Blank override projects as null, not an empty string.
        settings.RemoteAccess.UserComputerProfileOverride = "   ";
        Assert.Null(settings.BuildConfiguration().UserComputerProfileOverride);
    }

    [AvaloniaFact]
    public void Settings_WithoutProfileController_HasNoProfileSection()
    {
        var service = new ConfigurationPersistenceService(CreateTempConfigPath());
        var settings = new WorkspacesSettingsViewModel(service);

        Assert.Null(settings.ProfileAppearance);
        Assert.DoesNotContain(settings.Sections, section => section.Title == "Profile");
    }

    [AvaloniaFact]
    public void Settings_WithProfileController_AddsLiveProfileSection()
    {
        var service = new ConfigurationPersistenceService(CreateTempConfigPath());
        var controller = new FakeProfileAppearanceController();

        var settings = new WorkspacesSettingsViewModel(service, new WorkspacesConfiguration(), controller);

        Assert.NotNull(settings.ProfileAppearance);
        Assert.Same(controller, settings.ProfileAppearance!.Controller);

        var profileSection = Assert.Single(settings.Sections, section => section.Title == "Profile");
        Assert.Same(settings.ProfileAppearance, profileSection.Content);
    }

    [AvaloniaFact]
    public void Settings_WithLogDirectoryProvider_AddsLogsSection()
    {
        var service = new ConfigurationPersistenceService(CreateTempConfigPath());
        var logDirectoryProvider = new FakeLogDirectoryProvider("C:/temp/phantom-logs");
        var processLauncher = new Mock<IProcessLauncher>().Object;

        var settings = new WorkspacesSettingsViewModel(
            service,
            new WorkspacesConfiguration(),
            profileAppearance: null,
            updateController: null,
            updateDispatch: null,
            logDirectoryProvider: logDirectoryProvider,
            processLauncher: processLauncher);

        Assert.NotNull(settings.Logs);
        var logsSection = Assert.Single(settings.Sections, section => section.Title == "Logs");
        Assert.Same(settings.Logs, logsSection.Content);
    }

    [AvaloniaFact]
    public void Settings_WithoutLogDirectoryProvider_HasNoLogsSection()
    {
        var service = new ConfigurationPersistenceService(CreateTempConfigPath());
        var settings = new WorkspacesSettingsViewModel(service);

        Assert.Null(settings.Logs);
        Assert.DoesNotContain(settings.Sections, section => section.Title == "Logs");
    }

    [AvaloniaFact]
    public void Logs_LogDirectory_ReflectsProviderPath()
    {
        var provider = new FakeLogDirectoryProvider("D:/somewhere/logs");
        var launcher = new Mock<IProcessLauncher>().Object;

        var logs = new LogsSettingsViewModel(provider, launcher);

        Assert.Equal("D:/somewhere/logs", logs.LogDirectory);
    }

    [AvaloniaFact]
    public void Logs_LogDirectory_IsReadOnly()
    {
        // Reflection guarantees the property has no public setter, so no UI binding can mutate it.
        var property = typeof(LogsSettingsViewModel).GetProperty(nameof(LogsSettingsViewModel.LogDirectory));

        Assert.NotNull(property);
        Assert.True(property!.CanRead);
        Assert.Null(property.SetMethod);
    }

    [AvaloniaFact]
    public void Logs_OpenLogDirectoryCommand_LaunchesFileBrowserWithLogDirectory()
    {
        var provider = new FakeLogDirectoryProvider("C:/logs/here");
        ProcessStartRequest? captured = null;
        var launcher = new Mock<IProcessLauncher>();
        launcher
            .Setup(l => l.Start(It.IsAny<ProcessStartRequest>()))
            .Callback<ProcessStartRequest>(request => captured = request)
            .Returns(Mock.Of<IProcessHandle>());

        var logs = new LogsSettingsViewModel(provider, launcher.Object);
        logs.OpenLogDirectoryCommand.Execute(null);

        launcher.Verify(l => l.Start(It.IsAny<ProcessStartRequest>()), Times.Once);
        Assert.NotNull(captured);
        Assert.Contains("C:/logs/here", captured!.Arguments);
    }

    [AvaloniaFact]
    public void Logs_OpenLogDirectoryCommand_WhenDirectoryMissing_CreatesDirectoryBeforeLaunch()
    {
        var configurationPath = CreateTempConfigPath();
        var missingLogDir = Path.Combine(
            Path.GetTempPath(),
            $"phantom-logs-missing-{System.Guid.NewGuid():N}",
            "nested",
            "logs");

        Assert.False(Directory.Exists(missingLogDir));

        try
        {
            // Real provider lazily creates the directory on first LogDirectory access, matching production.
            var provider = new LogDirectoryProvider(
                new WorkspacesConfiguration { LogDirectory = missingLogDir },
                configurationPath);

            ProcessStartRequest? captured = null;
            var launcher = new Mock<IProcessLauncher>();
            launcher
                .Setup(l => l.Start(It.IsAny<ProcessStartRequest>()))
                .Callback<ProcessStartRequest>(request => captured = request)
                .Returns(Mock.Of<IProcessHandle>());

            var logs = new LogsSettingsViewModel(provider, launcher.Object);

            var exception = Record.Exception(() => logs.OpenLogDirectoryCommand.Execute(null));

            Assert.Null(exception);
            Assert.True(Directory.Exists(missingLogDir));
            launcher.Verify(l => l.Start(It.IsAny<ProcessStartRequest>()), Times.Once);
            Assert.NotNull(captured);
            Assert.Contains(missingLogDir, captured!.Arguments);
        }
        finally
        {
            var top = Path.GetDirectoryName(Path.GetDirectoryName(missingLogDir));
            if (top is not null && Directory.Exists(top))
            {
                Directory.Delete(top, recursive: true);
            }
        }
    }

    [AvaloniaFact]
    public void SettingsDialog_BindsLogsSectionViewModel_AndConstructs()
    {
        var service = new ConfigurationPersistenceService(CreateTempConfigPath());
        var provider = new FakeLogDirectoryProvider("C:/logs/bound");
        var launcher = new Mock<IProcessLauncher>().Object;

        var settings = new WorkspacesSettingsViewModel(
            service,
            new WorkspacesConfiguration(),
            profileAppearance: null,
            updateController: null,
            updateDispatch: null,
            logDirectoryProvider: provider,
            processLauncher: launcher);

        var dialog = new SettingsDialogWindow(settings);

        Assert.Same(settings, dialog.DataContext);
        Assert.NotNull(settings.Logs);
        // Select the Logs section so the ContentControl resolves the LogsSettingsViewModel DataTemplate.
        var logsSection = Assert.Single(settings.Sections, s => s.Title == "Logs");
        settings.SelectedSection = logsSection;
        Assert.Same(settings.Logs, settings.SelectedSection.Content);
    }

    private sealed class FakeLogDirectoryProvider : ILogDirectoryProvider
    {
        public FakeLogDirectoryProvider(string logDirectory)
        {
            this.LogDirectory = logDirectory;
        }

        public string LogDirectory { get; }
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

    private sealed class FakeProfileAppearanceController : IProfileAppearanceController
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public System.Collections.Generic.IReadOnlyList<string> ThemeNames { get; } = ["dark", "light"];

        public string SelectedThemeName { get; set; } = "dark";

        public bool IsDebuggingEnabled => false;

        public bool IsDebuggingDisabled => true;

        public RelayCommand SetDebuggingCommand { get; } = new(_ => { });

        private void RaisePropertyChanged(string propertyName)
            => this.PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
    }
}
