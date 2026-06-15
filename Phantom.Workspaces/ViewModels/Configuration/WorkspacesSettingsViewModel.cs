using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Configuration;

namespace Phantom.Workspaces.ViewModels.Configuration;

/// <summary>
/// Shared settings view model used by both the first-run installation wizard and the settings
/// dialog. It composes the repository and remote-access settings plus visual preferences over a
/// <see cref="WorkspacesConfiguration"/>, and persists changes.
/// </summary>
public sealed class WorkspacesSettingsViewModel : ViewModelBase
{
    private readonly ConfigurationPersistenceService persistenceService;
    private readonly WorkspacesConfiguration baseConfiguration;
    private string theme;

    /// <summary>Creates a settings view model starting from default configuration.</summary>
    public WorkspacesSettingsViewModel(ConfigurationPersistenceService persistenceService)
        : this(persistenceService, new WorkspacesConfiguration())
    {
    }

    /// <summary>Creates a settings view model over the supplied configuration.</summary>
    public WorkspacesSettingsViewModel(
        ConfigurationPersistenceService persistenceService,
        WorkspacesConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(persistenceService);
        ArgumentNullException.ThrowIfNull(configuration);
        this.persistenceService = persistenceService;
        this.baseConfiguration = configuration;
        this.Repository = new RepositoryConnectionSettingsViewModel(configuration.DataAccess);
        this.RemoteAccess = new RemoteAccessSettingsViewModel(
            configuration.RemoteHosting,
            configuration.DevTunnel);
        this.theme = configuration.Visual.Theme;

        this.Repository.PropertyChanged += this.OnSectionChanged;
        this.RemoteAccess.PropertyChanged += this.OnSectionChanged;
    }

    /// <summary>Repository data-access settings.</summary>
    public RepositoryConnectionSettingsViewModel Repository { get; }

    /// <summary>Remote-hosting and dev tunnel settings.</summary>
    public RemoteAccessSettingsViewModel RemoteAccess { get; }

    /// <summary>The selected visual theme.</summary>
    public string Theme
    {
        get => this.theme;
        set => this.SetProperty(ref this.theme, value);
    }

    /// <summary>Whether all sections are valid and the configuration can be saved.</summary>
    public bool CanSave => this.Repository.IsValid && this.RemoteAccess.IsValid;

    /// <summary>Builds the configuration represented by the current view-model state.</summary>
    public WorkspacesConfiguration BuildConfiguration() => this.baseConfiguration with
    {
        DataAccess = this.Repository.ToProfile(),
        RemoteHosting = this.RemoteAccess.ToRemoteHostingSettings(),
        DevTunnel = this.RemoteAccess.ToDevTunnelConfiguration(this.baseConfiguration.DevTunnel),
        Visual = this.baseConfiguration.Visual with { Theme = this.Theme },
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
        => this.RaisePropertyChanged(nameof(this.CanSave));
}
