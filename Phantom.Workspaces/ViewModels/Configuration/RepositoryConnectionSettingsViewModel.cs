using System;
using Phantom.Workspaces.Configuration;

namespace Phantom.Workspaces.ViewModels.Configuration;

/// <summary>
/// Editable view model for the repository data-access connection settings used by both the
/// installation wizard and the settings dialog.
/// </summary>
public sealed class RepositoryConnectionSettingsViewModel : ViewModelBase
{
    private DataAccessMode mode;
    private string? mongoContainerName;
    private string? mongoDataDirectory;
    private string? mongoDatabaseName;
    private string? mongoRootCollectionName;
    private int? mongoHostPort;
    private string? mongoConnectionStringSource;
    private string? webEndpoint;
    private string? devTunnelTokenSource;

    /// <summary>Creates a view model with default settings.</summary>
    public RepositoryConnectionSettingsViewModel()
        : this(new DataAccessConnectionProfile())
    {
    }

    /// <summary>Creates a view model initialized from an existing profile.</summary>
    public RepositoryConnectionSettingsViewModel(DataAccessConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        this.mode = profile.Mode;
        this.mongoContainerName = profile.MongoContainerName;
        this.mongoDataDirectory = profile.MongoDataDirectory;
        this.mongoDatabaseName = profile.MongoDatabaseName;
        this.mongoRootCollectionName = profile.MongoRootCollectionName;
        this.mongoHostPort = profile.MongoHostPort;
        this.mongoConnectionStringSource = profile.MongoConnectionStringSource;
        this.webEndpoint = profile.WebEndpoint;
        this.devTunnelTokenSource = profile.DevTunnelTokenSource;
    }

    /// <summary>The selected data-access mode.</summary>
    public DataAccessMode Mode
    {
        get => this.mode;
        set => this.SetValidatedProperty(ref this.mode, value);
    }

    /// <summary>Container name for the local MongoDB container mode.</summary>
    public string? MongoContainerName
    {
        get => this.mongoContainerName;
        set => this.SetValidatedProperty(ref this.mongoContainerName, value);
    }

    /// <summary>Host data directory mapped into the MongoDB container.</summary>
    public string? MongoDataDirectory
    {
        get => this.mongoDataDirectory;
        set => this.SetValidatedProperty(ref this.mongoDataDirectory, value);
    }

    /// <summary>MongoDB database name.</summary>
    public string? MongoDatabaseName
    {
        get => this.mongoDatabaseName;
        set => this.SetValidatedProperty(ref this.mongoDatabaseName, value);
    }

    /// <summary>Root collection name for entity storage.</summary>
    public string? MongoRootCollectionName
    {
        get => this.mongoRootCollectionName;
        set => this.SetValidatedProperty(ref this.mongoRootCollectionName, value);
    }

    /// <summary>Host port the MongoDB container is published on.</summary>
    public int? MongoHostPort
    {
        get => this.mongoHostPort;
        set => this.SetValidatedProperty(ref this.mongoHostPort, value);
    }

    /// <summary>Source name for the remote MongoDB connection string (never the raw value).</summary>
    public string? MongoConnectionStringSource
    {
        get => this.mongoConnectionStringSource;
        set => this.SetValidatedProperty(ref this.mongoConnectionStringSource, value);
    }

    /// <summary>Absolute web endpoint URL for web / dev tunnel modes.</summary>
    public string? WebEndpoint
    {
        get => this.webEndpoint;
        set => this.SetValidatedProperty(ref this.webEndpoint, value);
    }

    /// <summary>Source name for the dev tunnel access token (never the raw token).</summary>
    public string? DevTunnelTokenSource
    {
        get => this.devTunnelTokenSource;
        set => this.SetValidatedProperty(ref this.devTunnelTokenSource, value);
    }

    /// <summary>Whether the current settings are complete and valid for the selected mode.</summary>
    public bool IsValid => this.Mode switch
    {
        DataAccessMode.LocalMongoContainer =>
            !string.IsNullOrWhiteSpace(this.MongoContainerName)
            && !string.IsNullOrWhiteSpace(this.MongoRootCollectionName),
        DataAccessMode.RemoteMongo =>
            !string.IsNullOrWhiteSpace(this.MongoConnectionStringSource),
        DataAccessMode.Web or DataAccessMode.DevTunnelWeb =>
            Uri.TryCreate(this.WebEndpoint, UriKind.Absolute, out _),
        _ => false,
    };

    /// <summary>Projects the current settings into a <see cref="DataAccessConnectionProfile"/>.</summary>
    public DataAccessConnectionProfile ToProfile() => new()
    {
        Mode = this.Mode,
        MongoContainerName = this.MongoContainerName,
        MongoDataDirectory = this.MongoDataDirectory,
        MongoDatabaseName = this.MongoDatabaseName,
        MongoRootCollectionName = this.MongoRootCollectionName,
        MongoHostPort = this.MongoHostPort,
        MongoConnectionStringSource = this.MongoConnectionStringSource,
        WebEndpoint = this.WebEndpoint,
        DevTunnelTokenSource = this.DevTunnelTokenSource,
    };

    private void SetValidatedProperty<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (this.SetProperty(ref field, value, propertyName))
        {
            this.RaisePropertyChanged(nameof(this.IsValid));
        }
    }
}
