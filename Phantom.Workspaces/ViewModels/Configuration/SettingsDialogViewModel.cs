using System;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Configuration;

namespace Phantom.Workspaces.ViewModels.Configuration;

/// <summary>
/// Hosts the editable settings categories (repository, remote access, visual) over a shared
/// <see cref="WorkspacesConfiguration"/>, and persists changes.
/// </summary>
public sealed class SettingsDialogViewModel : ViewModelBase
{
    private readonly ConfigurationPersistenceService persistenceService;
    private readonly WorkspacesConfiguration baseConfiguration;
    private string theme;

    /// <summary>Creates a settings dialog over the supplied configuration.</summary>
    public SettingsDialogViewModel(
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

    /// <summary>Repository data-access settings category.</summary>
    public RepositoryConnectionSettingsViewModel Repository { get; }

    /// <summary>Remote access / hosting settings category.</summary>
    public RemoteAccessSettingsViewModel RemoteAccess { get; }

    /// <summary>The selected visual theme.</summary>
    public string Theme
    {
        get => this.theme;
        set => this.SetProperty(ref this.theme, value);
    }

    /// <summary>Whether all categories are valid and settings can be saved.</summary>
    public bool CanSave => this.Repository.IsValid && this.RemoteAccess.IsValid;

    /// <summary>Builds the configuration represented by the current category state.</summary>
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

    private void OnSectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => this.RaisePropertyChanged(nameof(this.CanSave));
}
