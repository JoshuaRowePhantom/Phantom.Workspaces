using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.ViewModels.Configuration;

/// <summary>
/// Shared settings view model used by both the first-run installation wizard and the settings
/// dialog. It composes the repository and remote-access settings plus visual preferences over a
/// <see cref="WorkspacesConfiguration"/>, and persists changes. The settings dialog renders the
/// <see cref="Sections"/> in a master-detail layout (section list on the left, the selected
/// section's view model on the right).
/// </summary>
public sealed class WorkspacesSettingsViewModel : ViewModelBase
{
    private readonly ConfigurationPersistenceService persistenceService;
    private readonly WorkspacesConfiguration baseConfiguration;
    private SettingsSectionViewModel selectedSection;

    /// <summary>Creates a settings view model starting from default configuration.</summary>
    public WorkspacesSettingsViewModel(ConfigurationPersistenceService persistenceService)
        : this(persistenceService, new WorkspacesConfiguration())
    {
    }

    /// <summary>Creates a settings view model over the supplied configuration.</summary>
    public WorkspacesSettingsViewModel(
        ConfigurationPersistenceService persistenceService,
        WorkspacesConfiguration configuration,
        IProfileAppearanceController? profileAppearance = null,
        Services.Updates.IUpdateController? updateController = null,
        Action<Action>? updateDispatch = null,
        Services.Logging.ILogDirectoryProvider? logDirectoryProvider = null,
        Phantom.Workspaces.Install.IProcessLauncher? processLauncher = null)
    {
        ArgumentNullException.ThrowIfNull(persistenceService);
        ArgumentNullException.ThrowIfNull(configuration);
        this.persistenceService = persistenceService;
        this.baseConfiguration = configuration;
        this.Repository = new RepositoryConnectionSettingsViewModel(configuration.DataAccess);
        this.RemoteAccess = new RemoteAccessSettingsViewModel(
            configuration.RemoteHosting,
            configuration.DevTunnel,
            configuration.UserComputerProfileOverride);

        this.Repository.PropertyChanged += this.OnSectionChanged;
        this.RemoteAccess.PropertyChanged += this.OnSectionChanged;

        var sections = new List<SettingsSectionViewModel>
        {
            new("Repository", this.Repository),
            new("Remote access", this.RemoteAccess),
        };

        // When opened from the running application, surface the live profile theme/debugging controls
        // as a Profile section so those settings remain reachable from the unified settings dialog.
        if (profileAppearance is not null)
        {
            this.ProfileAppearance = new ProfileAppearanceSettingsViewModel(profileAppearance);
            sections.Add(new SettingsSectionViewModel("Profile", this.ProfileAppearance));
        }

        // The Updates section is likewise only meaningful for a running, installed application that
        // has a live update controller; the installation wizard has nothing to update.
        if (updateController is not null)
        {
            this.Updates = new UpdateSettingsViewModel(updateController, configuration.Update, updateDispatch);
            sections.Add(new SettingsSectionViewModel("Updates", this.Updates));
        }

        // The Logs section requires both the process log-directory provider and a process launcher
        // (both are only available in the running application, not the first-run installation wizard).
        if (logDirectoryProvider is not null && processLauncher is not null)
        {
            this.Logs = new LogsSettingsViewModel(logDirectoryProvider, processLauncher);
            sections.Add(new SettingsSectionViewModel("Logs", this.Logs));
        }

        this.Sections = sections;
        this.selectedSection = this.Sections[0];
    }

    /// <summary>Repository data-access settings.</summary>
    public RepositoryConnectionSettingsViewModel Repository { get; }

    /// <summary>Remote-hosting and dev tunnel settings.</summary>
    public RemoteAccessSettingsViewModel RemoteAccess { get; }

    /// <summary>
    /// Live profile theme/debugging section, present only when the dialog is opened from the running
    /// application (the installation wizard has no running profile to control).
    /// </summary>
    public ProfileAppearanceSettingsViewModel? ProfileAppearance { get; }

    /// <summary>
    /// Application updates section, present only when the dialog is opened from the running, installed
    /// application (the installation wizard has nothing to update).
    /// </summary>
    public UpdateSettingsViewModel? Updates { get; }

    /// <summary>
    /// Logs section, present only when the dialog is opened from the running application (the
    /// installation wizard has no <see cref="Services.Logging.ILogDirectoryProvider"/> to bind to).
    /// </summary>
    public LogsSettingsViewModel? Logs { get; }

    /// <summary>The settings sections shown in the dialog's master-detail layout.</summary>
    public IReadOnlyList<SettingsSectionViewModel> Sections { get; }

    /// <summary>The currently selected settings section.</summary>
    public SettingsSectionViewModel SelectedSection
    {
        get => this.selectedSection;
        set
        {
            if (value is not null)
            {
                this.SetProperty(ref this.selectedSection, value);
            }
        }
    }

    /// <summary>Whether all sections are valid and the configuration can be saved.</summary>
    public bool CanSave => this.Repository.IsValid && this.RemoteAccess.IsValid;

    /// <summary>
    /// Human-readable messages naming the specific missing/invalid fields that make
    /// <see cref="CanSave"/> return <see langword="false"/>. Empty when everything is valid.
    /// Rendered in the setup wizard above the Complete/Cancel buttons so users know why the
    /// button is disabled and what to fix.
    /// </summary>
    public IReadOnlyList<string> ValidationMessages
    {
        get
        {
            var messages = new List<string>();
            var modeMessage = this.Repository.ActiveSettings.ValidationMessage;
            if (!string.IsNullOrEmpty(modeMessage))
            {
                messages.Add(modeMessage);
            }
            var remoteMessage = this.RemoteAccess.ValidationMessage;
            if (!string.IsNullOrEmpty(remoteMessage))
            {
                messages.Add(remoteMessage);
            }
            return messages;
        }
    }

    /// <summary>Builds the configuration represented by the current view-model state.</summary>
    public WorkspacesConfiguration BuildConfiguration() => this.baseConfiguration with
    {
        DataAccess = this.Repository.ToProfile(),
        RemoteHosting = this.RemoteAccess.ToRemoteHostingSettings(),
        DevTunnel = this.RemoteAccess.ToDevTunnelConfiguration(this.baseConfiguration.DevTunnel),
        Update = this.Updates is null
            ? this.baseConfiguration.Update
            : this.Updates.ToSettings(this.baseConfiguration.Update),
        UserComputerProfileOverride = string.IsNullOrWhiteSpace(this.RemoteAccess.UserComputerProfileOverride)
            ? null
            : this.RemoteAccess.UserComputerProfileOverride,
    };

    /// <summary>Builds and persists the configuration, returning the saved configuration.</summary>
    public async Task<WorkspacesConfiguration> SaveAsync(
        string? path = null,
        CancellationToken cancellationToken = default)
    {
        if (!this.CanSave)
        {
            throw new InvalidOperationException("Settings are incomplete or invalid.");
        }

        var configuration = this.BuildConfiguration();
        await this.persistenceService.SaveAsync(configuration, path, cancellationToken).ConfigureAwait(false);
        return configuration;
    }

    private void OnSectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        this.RaisePropertyChanged(nameof(this.CanSave));
        this.RaisePropertyChanged(nameof(this.ValidationMessages));
    }
}
