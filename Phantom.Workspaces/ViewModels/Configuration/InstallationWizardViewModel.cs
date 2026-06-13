using System;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Configuration;

namespace Phantom.Workspaces.ViewModels.Configuration;

/// <summary>
/// Orchestrates the first-run installation setup, composing the repository and remote-access
/// settings and persisting the resulting <see cref="WorkspacesConfiguration"/>.
/// </summary>
public sealed class InstallationWizardViewModel : ViewModelBase
{
    private readonly ConfigurationPersistenceService persistenceService;
    private readonly WorkspacesConfiguration baseConfiguration;

    /// <summary>Creates a wizard starting from default configuration.</summary>
    public InstallationWizardViewModel(ConfigurationPersistenceService persistenceService)
        : this(persistenceService, new WorkspacesConfiguration())
    {
    }

    /// <summary>Creates a wizard starting from the supplied base configuration.</summary>
    public InstallationWizardViewModel(
        ConfigurationPersistenceService persistenceService,
        WorkspacesConfiguration baseConfiguration)
    {
        ArgumentNullException.ThrowIfNull(persistenceService);
        ArgumentNullException.ThrowIfNull(baseConfiguration);
        this.persistenceService = persistenceService;
        this.baseConfiguration = baseConfiguration;
        this.Repository = new RepositoryConnectionSettingsViewModel(baseConfiguration.DataAccess);
        this.RemoteAccess = new RemoteAccessSettingsViewModel(
            baseConfiguration.RemoteHosting,
            baseConfiguration.DevTunnel);

        this.Repository.PropertyChanged += this.OnSectionChanged;
        this.RemoteAccess.PropertyChanged += this.OnSectionChanged;
    }

    /// <summary>Repository data-access settings.</summary>
    public RepositoryConnectionSettingsViewModel Repository { get; }

    /// <summary>Remote-hosting and dev tunnel settings.</summary>
    public RemoteAccessSettingsViewModel RemoteAccess { get; }

    /// <summary>Whether the wizard has enough valid input to complete.</summary>
    public bool CanComplete => this.Repository.IsValid && this.RemoteAccess.IsValid;

    /// <summary>Builds the configuration represented by the current wizard state.</summary>
    public WorkspacesConfiguration BuildConfiguration() => this.baseConfiguration with
    {
        DataAccess = this.Repository.ToProfile(),
        RemoteHosting = this.RemoteAccess.ToRemoteHostingSettings(),
        DevTunnel = this.RemoteAccess.ToDevTunnelConfiguration(this.baseConfiguration.DevTunnel),
    };

    /// <summary>Builds and persists the configuration, returning the saved configuration.</summary>
    public async Task<WorkspacesConfiguration> CompleteAsync(
        string? path = null,
        CancellationToken cancellationToken = default)
    {
        if (!this.CanComplete)
        {
            throw new InvalidOperationException("The installation wizard input is incomplete or invalid.");
        }

        var configuration = this.BuildConfiguration();
        await this.persistenceService.SaveAsync(configuration, path, cancellationToken).ConfigureAwait(false);
        return configuration;
    }

    private void OnSectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => this.RaisePropertyChanged(nameof(this.CanComplete));
}
