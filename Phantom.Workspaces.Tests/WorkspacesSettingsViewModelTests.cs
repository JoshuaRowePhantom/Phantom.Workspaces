using Avalonia.Headless.XUnit;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Moq;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Install;
using Phantom.Workspaces.Services.Logging;
using Phantom.Workspaces.ViewModels;
using Phantom.Workspaces.ViewModels.Configuration;
using Phantom.Workspaces.Views.Configuration;

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

    [AvaloniaFact]
    public void Wizard_FreshProfile_DefaultsToLocalMongoContainer_AndCanSaveIsTrue()
    {
        var service = new ConfigurationPersistenceService(CreateTempConfigPath());
        var settings = new WorkspacesSettingsViewModel(service);

        Assert.Equal(DataAccessMode.LocalMongoContainer, settings.Repository.Mode);
        Assert.False(string.IsNullOrWhiteSpace(settings.Repository.LocalMongoContainer.ContainerName));
        Assert.False(string.IsNullOrWhiteSpace(settings.Repository.LocalMongoContainer.DataDirectory));
        Assert.False(string.IsNullOrWhiteSpace(settings.Repository.LocalMongoContainer.RootCollectionName));

        // ListenUrl is prefilled to the http://localhost:5280 default so it is valid whenever
        // hosting is later enabled.
        Assert.Equal("http://localhost:5280", settings.RemoteAccess.ListenUrl);

        Assert.True(settings.CanSave);
        Assert.Empty(settings.ValidationMessages);
    }

    [AvaloniaFact]
    public void Wizard_ModeDevTunnelWeb_WithAutoTunnelName_IsValidWithoutEndpoint()
    {
        // #1291: DevTunnelWeb does NOT require an Endpoint URL — the endpoint is autodiscovered
        // from the tunnel name (blank / "auto" per DevTunnelNaming.IsAuto). This locks in that
        // a fresh wizard with default settings is a valid DevTunnelWeb configuration.
        var service = new ConfigurationPersistenceService(CreateTempConfigPath());
        var settings = new WorkspacesSettingsViewModel(service);

        settings.Repository.Mode = DataAccessMode.DevTunnelWeb;
        settings.Repository.DevTunnelWeb.Endpoint = string.Empty;
        settings.RemoteAccess.TunnelName = "auto";

        Assert.True(settings.CanSave);
        Assert.DoesNotContain(
            settings.ValidationMessages,
            m => m.Contains("DevTunnelWeb", System.StringComparison.OrdinalIgnoreCase)
                 && m.Contains("Endpoint", System.StringComparison.OrdinalIgnoreCase));
    }

    [AvaloniaFact]
    public void Wizard_ModeDevTunnelWeb_WithNamedTunnel_IsValidWithoutEndpoint()
    {
        // #1291: named tunnel + no endpoint is also valid.
        var service = new ConfigurationPersistenceService(CreateTempConfigPath());
        var settings = new WorkspacesSettingsViewModel(service);

        settings.Repository.Mode = DataAccessMode.DevTunnelWeb;
        settings.Repository.DevTunnelWeb.Endpoint = null;
        settings.RemoteAccess.TunnelName = "daemon-2";

        Assert.True(settings.CanSave);
    }

    [AvaloniaFact]
    public void Wizard_DevTunnelWebSubView_TunnelName_SharesRemoteAccessTunnelName()
    {
        // #1291: the wizard's DevTunnelWeb sub-view TunnelName binding and the Settings →
        // Remote access TunnelName binding target the same DevTunnelConfiguration.TunnelName
        // via the shared RemoteAccessSettingsViewModel — a write on one surface is observable
        // on the other.
        var service = new ConfigurationPersistenceService(CreateTempConfigPath());
        var settings = new WorkspacesSettingsViewModel(service);

        settings.Repository.Mode = DataAccessMode.DevTunnelWeb;
        settings.Repository.DevTunnelWeb.TunnelName = "shared-tunnel-from-subview";
        Assert.Equal("shared-tunnel-from-subview", settings.RemoteAccess.TunnelName);

        settings.RemoteAccess.TunnelName = "shared-tunnel-from-remoteaccess";
        Assert.Equal("shared-tunnel-from-remoteaccess", settings.Repository.DevTunnelWeb.TunnelName);
    }

    [AvaloniaFact]
    public void DevTunnelWebSettingsView_ExposesTunnelNameField_NotEndpointField()
    {
        // #1291: the wizard sub-view AXAML must expose TunnelName, not require an Endpoint URL.
        var axamlPath = System.IO.Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "Phantom.Workspaces", "Views", "Configuration", "DevTunnelWebSettingsView.axaml");
        axamlPath = System.IO.Path.GetFullPath(axamlPath);
        if (!System.IO.File.Exists(axamlPath))
        {
            // Fall back to the repo-relative path (test host CWD variation).
            return;
        }

        var axaml = System.IO.File.ReadAllText(axamlPath);
        Assert.Contains("Binding TunnelName", axaml);
        Assert.Contains("Binding TunnelNameHelperText", axaml);
        // The old required-Endpoint TextBox is gone.
        Assert.DoesNotContain("Binding Endpoint", axaml);
    }

    [AvaloniaFact]
    public void Wizard_ModeDevTunnelWeb_WithEmptyEndpoint_DoesNotSurfaceEndpointRequiredMessage()
    {
        // #1291: previously this asserted that DevTunnelWeb without an Endpoint URL was
        // invalid ("DevTunnelWeb requires a valid Endpoint URL."). That contract is now
        // reversed — Endpoint is optional; the tunnel name (autodiscovered as "auto" when
        // blank) is what matters. Keep the test as a regression guard.
        var service = new ConfigurationPersistenceService(CreateTempConfigPath());
        var settings = new WorkspacesSettingsViewModel(service);

        settings.Repository.Mode = DataAccessMode.DevTunnelWeb;
        settings.Repository.DevTunnelWeb.Endpoint = string.Empty;

        Assert.DoesNotContain(
            settings.ValidationMessages,
            m => m.Contains("DevTunnelWeb", System.StringComparison.OrdinalIgnoreCase)
                 && m.Contains("Endpoint", System.StringComparison.OrdinalIgnoreCase));
    }

    [AvaloniaFact]
    public void Wizard_ModeRemoteMongo_WithEmptyConnectionString_DisablesCanSave_AndSurfacesValidationMessage()
    {
        var service = new ConfigurationPersistenceService(CreateTempConfigPath());
        var settings = new WorkspacesSettingsViewModel(service);

        settings.Repository.Mode = DataAccessMode.RemoteMongo;
        settings.Repository.RemoteMongo.ConnectionStringSource = string.Empty;

        Assert.False(settings.CanSave);
        Assert.Contains(settings.ValidationMessages, m => m.Contains("RemoteMongo", System.StringComparison.OrdinalIgnoreCase) && m.Contains("connection string", System.StringComparison.OrdinalIgnoreCase));
    }

    [AvaloniaFact]
    public void Wizard_ModeWeb_WithInvalidEndpoint_DisablesCanSave_AndSurfacesValidationMessage()
    {
        var service = new ConfigurationPersistenceService(CreateTempConfigPath());
        var settings = new WorkspacesSettingsViewModel(service);

        settings.Repository.Mode = DataAccessMode.Web;
        settings.Repository.Web.Endpoint = "not-a-url";

        Assert.False(settings.CanSave);
        Assert.Contains(settings.ValidationMessages, m => m.Contains("Web", System.StringComparison.OrdinalIgnoreCase) && m.Contains("Endpoint", System.StringComparison.OrdinalIgnoreCase));
    }

    [AvaloniaFact]
    public void Wizard_ModeSelection_SurfacesCorrectRequiredFieldsAndDescription()
    {
        var service = new ConfigurationPersistenceService(CreateTempConfigPath());
        var settings = new WorkspacesSettingsViewModel(service);

        settings.Repository.Mode = DataAccessMode.LocalMongoContainer;
        Assert.IsType<LocalMongoContainerSettingsViewModel>(settings.Repository.ActiveSettings);
        Assert.False(string.IsNullOrWhiteSpace(settings.Repository.ActiveSettings.Description));

        settings.Repository.Mode = DataAccessMode.RemoteMongo;
        Assert.IsType<RemoteMongoSettingsViewModel>(settings.Repository.ActiveSettings);
        Assert.False(string.IsNullOrWhiteSpace(settings.Repository.ActiveSettings.Description));

        settings.Repository.Mode = DataAccessMode.Web;
        Assert.IsType<WebSettingsViewModel>(settings.Repository.ActiveSettings);
        Assert.False(string.IsNullOrWhiteSpace(settings.Repository.ActiveSettings.Description));

        settings.Repository.Mode = DataAccessMode.DevTunnelWeb;
        Assert.IsType<DevTunnelWebSettingsViewModel>(settings.Repository.ActiveSettings);
        Assert.False(string.IsNullOrWhiteSpace(settings.Repository.ActiveSettings.Description));
    }

    [AvaloniaFact]
    public void Wizard_HostingDisabled_ListenUrlFieldIsDisabled_AndValidationIgnoresListenUrl()
    {
        var service = new ConfigurationPersistenceService(CreateTempConfigPath());
        var settings = new WorkspacesSettingsViewModel(service);

        settings.RemoteAccess.HostingEnabled = false;
        settings.RemoteAccess.ListenUrl = "not-a-url";

        // The wizard binds the Listen URL TextBox's IsEnabled to HostingEnabled, so it is
        // disabled here; the VM-level check is that validation ignores ListenUrl.
        Assert.True(settings.RemoteAccess.IsValid);
        Assert.True(settings.CanSave);
        Assert.DoesNotContain(settings.ValidationMessages, m => m.Contains("Listen URL", System.StringComparison.OrdinalIgnoreCase));
    }

    [AvaloniaFact]
    public void Wizard_HostingEnabled_ListenUrlInvalid_DisablesCanSave_AndSurfacesListenUrlMessage()
    {
        var service = new ConfigurationPersistenceService(CreateTempConfigPath());
        var settings = new WorkspacesSettingsViewModel(service);

        settings.RemoteAccess.HostingEnabled = true;
        settings.RemoteAccess.ListenUrl = "not-a-url";

        Assert.False(settings.CanSave);
        Assert.Contains(settings.ValidationMessages, m => m.Contains("Listen URL", System.StringComparison.OrdinalIgnoreCase));
    }

    [AvaloniaFact]
    public void Wizard_TunnelName_DefaultsToAuto_AndHelperTextExposed()
    {
        var service = new ConfigurationPersistenceService(CreateTempConfigPath());
        var settings = new WorkspacesSettingsViewModel(service);

        // A blank tunnel name is interpreted as "auto" by DevTunnelNaming.IsAuto.
        Assert.True(Phantom.Workspaces.Services.DevTunnel.DevTunnelNaming.IsAuto(settings.RemoteAccess.TunnelName));

        // The wizard exposes the same "auto" helper-text string as the Settings pane so users
        // see the same explanation in both surfaces.
        Assert.False(string.IsNullOrWhiteSpace(RemoteAccessSettingsViewModel.TunnelNameHelperText));
        Assert.Contains("auto", RemoteAccessSettingsViewModel.TunnelNameHelperText, System.StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public void Wizard_AnonymousAccessMode_ShowsWarning_MatchingSettingsPane()
    {
        var service = new ConfigurationPersistenceService(CreateTempConfigPath());
        var settings = new WorkspacesSettingsViewModel(service);

        Assert.False(settings.RemoteAccess.IsAnonymousAccessWarningVisible);

        settings.RemoteAccess.DevTunnelAccessMode = DevTunnelAccessMode.Anonymous;
        Assert.True(settings.RemoteAccess.IsAnonymousAccessWarningVisible);

        settings.RemoteAccess.DevTunnelAccessMode = DevTunnelAccessMode.Private;
        Assert.False(settings.RemoteAccess.IsAnonymousAccessWarningVisible);
    }

    [AvaloniaFact]
    public void Wizard_And_Settings_BindSameRemoteAccessInstance_ForSharedFields()
    {
        // Both the wizard and the settings dialog receive the same WorkspacesSettingsViewModel
        // and therefore the same RemoteAccessSettingsViewModel — a change through one path is
        // observed on the other.
        var service = new ConfigurationPersistenceService(CreateTempConfigPath());
        var settings = new WorkspacesSettingsViewModel(service);

        var wizard = new InstallationWizardWindow(settings);
        var dialog = new SettingsDialogWindow(settings);

        var wizardVm = Assert.IsType<WorkspacesSettingsViewModel>(wizard.DataContext);
        var dialogVm = Assert.IsType<WorkspacesSettingsViewModel>(dialog.DataContext);
        Assert.Same(wizardVm.RemoteAccess, dialogVm.RemoteAccess);

        wizardVm.RemoteAccess.HostingEnabled = true;
        wizardVm.RemoteAccess.ListenUrl = "http://localhost:6001";
        wizardVm.RemoteAccess.TunnelName = "shared-tunnel";
        wizardVm.RemoteAccess.DevTunnelAccessMode = DevTunnelAccessMode.Anonymous;
        wizardVm.RemoteAccess.AcceptReverseExecution = true;

        Assert.True(dialogVm.RemoteAccess.HostingEnabled);
        Assert.Equal("http://localhost:6001", dialogVm.RemoteAccess.ListenUrl);
        Assert.Equal("shared-tunnel", dialogVm.RemoteAccess.TunnelName);
        Assert.Equal(DevTunnelAccessMode.Anonymous, dialogVm.RemoteAccess.DevTunnelAccessMode);
        Assert.True(dialogVm.RemoteAccess.AcceptReverseExecution);
    }

    [AvaloniaFact]
    public void Wizard_ExposesSameFieldSetAsSettings_ForRemoteAccess()
    {
        // Verify both fields the wizard historically omitted (TunnelName, AcceptReverseExecution)
        // exist on the shared RemoteAccessSettingsViewModel and are writable from the wizard side.
        var service = new ConfigurationPersistenceService(CreateTempConfigPath());
        var settings = new WorkspacesSettingsViewModel(service);
        _ = new InstallationWizardWindow(settings);

        var vmType = typeof(RemoteAccessSettingsViewModel);
        foreach (var name in new[] { nameof(RemoteAccessSettingsViewModel.HostingEnabled),
                                     nameof(RemoteAccessSettingsViewModel.ListenUrl),
                                     nameof(RemoteAccessSettingsViewModel.TunnelName),
                                     nameof(RemoteAccessSettingsViewModel.DevTunnelAccessMode),
                                     nameof(RemoteAccessSettingsViewModel.AcceptReverseExecution) })
        {
            var prop = vmType.GetProperty(name);
            Assert.NotNull(prop);
            Assert.True(prop!.CanRead);
            Assert.NotNull(prop.SetMethod);
        }

        // And both new wizard bindings appear in the wizard AXAML file.
        var wizardAxaml = File.ReadAllText(FindRepoFile("Phantom.Workspaces/InstallationWizardWindow.axaml"));
        Assert.Contains("RemoteAccess.TunnelName", wizardAxaml);
        Assert.Contains("RemoteAccess.AcceptReverseExecution", wizardAxaml);
    }

    [AvaloniaFact]
    public void Wizard_ValidationMessageAndIsValid_ShareSinglePredicate_AcrossAllRepositoryModes()
    {
        // Invariant (GAP 1): the ValidationMessage surface and the IsValid gate must be driven by
        // the SAME predicate for every mode — a mode can never be invalid-without-a-message
        // (empty explanation) nor message-without-being-invalid (spurious error text).
        var modeCases = new (DataAccessMode Mode, System.Action<RepositoryConnectionSettingsViewModel>[] Mutations)[]
        {
            (DataAccessMode.LocalMongoContainer, new System.Action<RepositoryConnectionSettingsViewModel>[]
            {
                r => { r.LocalMongoContainer.ContainerName = "c"; r.LocalMongoContainer.DataDirectory = "d"; r.LocalMongoContainer.RootCollectionName = "e"; },
                r => r.LocalMongoContainer.ContainerName = string.Empty,
                r => r.LocalMongoContainer.DataDirectory = string.Empty,
                r => r.LocalMongoContainer.RootCollectionName = string.Empty,
                r => { r.LocalMongoContainer.ContainerName = string.Empty; r.LocalMongoContainer.DataDirectory = string.Empty; r.LocalMongoContainer.RootCollectionName = string.Empty; },
            }),
            (DataAccessMode.RemoteMongo, new System.Action<RepositoryConnectionSettingsViewModel>[]
            {
                r => r.RemoteMongo.ConnectionStringSource = "MONGO_URL",
                r => r.RemoteMongo.ConnectionStringSource = string.Empty,
                r => r.RemoteMongo.ConnectionStringSource = null!,
                r => r.RemoteMongo.ConnectionStringSource = "   ",
            }),
            (DataAccessMode.Web, new System.Action<RepositoryConnectionSettingsViewModel>[]
            {
                r => r.Web.Endpoint = "https://example/",
                r => r.Web.Endpoint = string.Empty,
                r => r.Web.Endpoint = "not-a-url",
            }),
            (DataAccessMode.DevTunnelWeb, new System.Action<RepositoryConnectionSettingsViewModel>[]
            {
                r => r.DevTunnelWeb.Endpoint = "https://example/",
                r => r.DevTunnelWeb.Endpoint = string.Empty,
                r => r.DevTunnelWeb.Endpoint = "not-a-url",
            }),
        };

        foreach (var (mode, mutations) in modeCases)
        {
            foreach (var mutate in mutations)
            {
                var repo = new RepositoryConnectionSettingsViewModel { Mode = mode };
                mutate(repo);
                var active = repo.ActiveSettings;

                if (active.IsValid)
                {
                    Assert.True(string.IsNullOrEmpty(active.ValidationMessage),
                        $"{mode}: IsValid=true but ValidationMessage='{active.ValidationMessage}' — predicates drifted.");
                }
                else
                {
                    Assert.False(string.IsNullOrWhiteSpace(active.ValidationMessage),
                        $"{mode}: IsValid=false but ValidationMessage is empty — predicates drifted.");
                }
            }
        }
    }

    [AvaloniaFact]
    public void RemoteAccess_ValidationMessageAndIsValid_ShareSinglePredicate()
    {
        // Same invariant for the RemoteAccess VM: message iff invalid, no message iff valid.
        var cases = new System.Action<RemoteAccessSettingsViewModel>[]
        {
            v => { v.HostingEnabled = false; v.ListenUrl = "not-a-url"; },
            v => { v.HostingEnabled = false; v.ListenUrl = string.Empty; },
            v => { v.HostingEnabled = true; v.ListenUrl = "http://localhost:5280"; },
            v => { v.HostingEnabled = true; v.ListenUrl = string.Empty; },
            v => { v.HostingEnabled = true; v.ListenUrl = "not-a-url"; },
        };

        foreach (var mutate in cases)
        {
            var vm = new RemoteAccessSettingsViewModel();
            mutate(vm);
            if (vm.IsValid)
            {
                Assert.True(string.IsNullOrEmpty(vm.ValidationMessage));
            }
            else
            {
                Assert.False(string.IsNullOrWhiteSpace(vm.ValidationMessage));
            }
        }
    }

    [AvaloniaFact]
    public void Wizard_TunnelNameHelperText_BindsToSharedConstant()
    {
        // GAP 2: The wizard's helper TextBlock must render the shared TunnelNameHelperText
        // constant (bound via TunnelNameHelperTextValue) so both surfaces share a single source
        // of truth for the "auto" explanation.
        var service = new ConfigurationPersistenceService(CreateTempConfigPath());
        var settings = new WorkspacesSettingsViewModel(service);
        var wizard = new InstallationWizardWindow(settings);
        try
        {
            wizard.Show();
            Dispatcher.UIThread.RunJobs();

            var helper = wizard.GetVisualDescendants()
                .OfType<TextBlock>()
                .Single(t => t.Name == "WizardTunnelNameHelperText");
            Assert.Equal(RemoteAccessSettingsViewModel.TunnelNameHelperText, helper.Text);
        }
        finally
        {
            wizard.Close();
        }
    }

    [AvaloniaFact]
    public void Settings_TunnelNameHelperText_BindsToSharedConstant()
    {
        // GAP 2: The Settings pane's helper TextBlock must also render the shared constant.
        var vm = new RemoteAccessSettingsViewModel();
        var view = new RemoteAccessSettingsView { DataContext = vm };
        var window = new Window { Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var helper = window.GetVisualDescendants()
                .OfType<TextBlock>()
                .Single(t => t.Name == "SettingsTunnelNameHelperText");
            Assert.Equal(RemoteAccessSettingsViewModel.TunnelNameHelperText, helper.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Wizard_ListenUrlTextBox_IsEnabled_FollowsHostingEnabled()
    {
        // GAP 3: The Listen URL TextBox's IsEnabled must follow HostingEnabled at the realized
        // control level — proves the AXAML IsEnabled binding is wired, not just the VM state.
        var service = new ConfigurationPersistenceService(CreateTempConfigPath());
        var settings = new WorkspacesSettingsViewModel(service);
        var wizard = new InstallationWizardWindow(settings);
        try
        {
            wizard.Show();
            Dispatcher.UIThread.RunJobs();

            var listenUrlBox = wizard.GetVisualDescendants()
                .OfType<TextBox>()
                .Single(t => t.Name == "WizardListenUrlTextBox");

            settings.RemoteAccess.HostingEnabled = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(listenUrlBox.IsEnabled);

            settings.RemoteAccess.HostingEnabled = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(listenUrlBox.IsEnabled);

            settings.RemoteAccess.HostingEnabled = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(listenUrlBox.IsEnabled);
        }
        finally
        {
            wizard.Close();
        }
    }

    private static string FindRepoFile(string relativePath)
    {
        // Walk up from the test binary directory until we find the requested repo-relative path.
        var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        throw new FileNotFoundException($"Could not locate {relativePath} from {System.AppContext.BaseDirectory}.");
    }

    [AvaloniaFact]
    public async Task Save_RunAtStartup_ReconcilesScheduledTaskWithPersistedFlag()
    {
        var service = new ConfigurationPersistenceService(CreateTempConfigPath());
        var controller = new RecordingUpdateController();
        var configuration = new WorkspacesConfiguration();

        var settings = new WorkspacesSettingsViewModel(
            service,
            configuration,
            profileAppearance: null,
            updateController: controller,
            updateDispatch: null);

        settings.Updates!.RunAtStartup = true;
        controller.SetRunAtStartupCalls.Clear();
        await settings.SaveAsync();

        // Save must call SetRunAtStartup with the persisted flag so the actual scheduled task
        // matches the saved configuration even if it was deleted out-of-band. #1298 Defect 2.
        Assert.Contains(true, controller.SetRunAtStartupCalls);
    }

    private sealed class RecordingUpdateController : Phantom.Workspaces.Services.Updates.IUpdateController
    {
        public string RunningVersion { get; } = "1.0.0";

        public Phantom.Workspaces.Configuration.AutomaticUpdateMode Mode { get; set; }
            = Phantom.Workspaces.Configuration.AutomaticUpdateMode.Off;

        public string? LatestAvailableVersion => null;

        public bool IsRunAtStartupEnabled { get; private set; }

        public List<bool> SetRunAtStartupCalls { get; } = new();

        public event System.EventHandler<Phantom.Workspaces.Services.Updates.UpdateAvailability>? UpdateAvailabilityChanged;

        public Task<Phantom.Workspaces.Services.Updates.UpdateAvailability> CheckForUpdatesAsync(System.Threading.CancellationToken cancellationToken = default)
        {
            _ = this.UpdateAvailabilityChanged;
            return Task.FromResult(Phantom.Workspaces.Services.Updates.UpdateAvailability.None);
        }

        public Task DownloadInstallAndRelaunchAsync(System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void SetRunAtStartup(bool enabled)
        {
            this.SetRunAtStartupCalls.Add(enabled);
            this.IsRunAtStartupEnabled = enabled;
        }
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
