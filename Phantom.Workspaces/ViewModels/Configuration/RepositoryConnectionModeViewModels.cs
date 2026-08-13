using System;
using System.IO;
using Phantom.Workspaces.Configuration;

namespace Phantom.Workspaces.ViewModels.Configuration;

/// <summary>Settings for the local MongoDB container connection mode.</summary>
public sealed class LocalMongoContainerSettingsViewModel : RepositoryConnectionModeViewModel
{
    private string? containerName;
    private string? dataDirectory;
    private string? databaseName;
    private string? rootCollectionName;
    private int? hostPort;

    /// <summary>Creates the view model from an existing profile.</summary>
    public LocalMongoContainerSettingsViewModel(DataAccessConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        this.containerName = string.IsNullOrWhiteSpace(profile.MongoContainerName)
            ? DefaultContainerName
            : profile.MongoContainerName;
        this.dataDirectory = string.IsNullOrWhiteSpace(profile.MongoDataDirectory)
            ? GetDefaultDataDirectory()
            : profile.MongoDataDirectory;
        this.databaseName = string.IsNullOrWhiteSpace(profile.MongoDatabaseName)
            ? DefaultDatabaseName
            : profile.MongoDatabaseName;
        this.rootCollectionName = string.IsNullOrWhiteSpace(profile.MongoRootCollectionName)
            ? DefaultRootCollectionName
            : profile.MongoRootCollectionName;
        this.hostPort = profile.MongoHostPort;
    }

    /// <summary>The default container name pre-filled when a profile does not specify one.</summary>
    public const string DefaultContainerName = "mongodb";

    /// <summary>The default root collection name pre-filled when a profile does not specify one.</summary>
    public const string DefaultRootCollectionName = "entities";

    /// <summary>The default database name pre-filled when a profile does not specify one.</summary>
    public const string DefaultDatabaseName = "phantom-workspaces";

    /// <summary>
    /// The wizard/GUI default Mongo data directory used to pre-fill the field when a profile does not
    /// specify one: the current user's home directory plus <c>Phantom.Workspaces/Mongo</c> (local to
    /// the user and clearly Phantom.Workspaces-purposed). The data layer applies no default; it is
    /// configured here in the GUI and persisted into the profile.
    /// </summary>
    public static string GetDefaultDataDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Phantom.Workspaces",
            "Mongo");
    }

    /// <inheritdoc />
    public override DataAccessMode Mode => DataAccessMode.LocalMongoContainer;

    /// <summary>Container name.</summary>
    public string? ContainerName
    {
        get => this.containerName;
        set => this.SetValidatedProperty(ref this.containerName, value);
    }

    /// <summary>Host data directory mapped into the container.</summary>
    public string? DataDirectory
    {
        get => this.dataDirectory;
        set => this.SetValidatedProperty(ref this.dataDirectory, value);
    }

    /// <summary>Database name.</summary>
    public string? DatabaseName
    {
        get => this.databaseName;
        set => this.SetValidatedProperty(ref this.databaseName, value);
    }

    /// <summary>Root collection name.</summary>
    public string? RootCollectionName
    {
        get => this.rootCollectionName;
        set => this.SetValidatedProperty(ref this.rootCollectionName, value);
    }

    /// <summary>Host port the container is published on.</summary>
    public int? HostPort
    {
        get => this.hostPort;
        set => this.SetValidatedProperty(ref this.hostPort, value);
    }

    /// <inheritdoc />
    public override string Description =>
        "Recommended for first-time and offline use. Runs MongoDB locally in Docker Desktop. Nothing to configure remotely.";

    /// <inheritdoc />
    public override string? ValidationMessage
    {
        get
        {
            if (string.IsNullOrWhiteSpace(this.ContainerName))
            {
                return "LocalMongoContainer requires a container name.";
            }
            if (string.IsNullOrWhiteSpace(this.DataDirectory))
            {
                return "LocalMongoContainer requires a data directory.";
            }
            if (string.IsNullOrWhiteSpace(this.RootCollectionName))
            {
                return "LocalMongoContainer requires a root collection name.";
            }
            return null;
        }
    }

    /// <inheritdoc />
    public override DataAccessConnectionProfile ToProfile() => new()
    {
        Mode = this.Mode,
        MongoContainerName = this.ContainerName,
        MongoDataDirectory = this.DataDirectory,
        MongoDatabaseName = this.DatabaseName,
        MongoRootCollectionName = this.RootCollectionName,
        MongoHostPort = this.HostPort,
    };
}

/// <summary>Settings for the remote MongoDB connection mode.</summary>
public sealed class RemoteMongoSettingsViewModel : RepositoryConnectionModeViewModel
{
    private string? connectionStringSource;
    private string? databaseName;
    private string? rootCollectionName;

    /// <summary>Creates the view model from an existing profile.</summary>
    public RemoteMongoSettingsViewModel(DataAccessConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        this.connectionStringSource = profile.MongoConnectionStringSource;
        this.databaseName = profile.MongoDatabaseName;
        this.rootCollectionName = profile.MongoRootCollectionName;
    }

    /// <inheritdoc />
    public override DataAccessMode Mode => DataAccessMode.RemoteMongo;

    /// <summary>Source name for the connection string (never the raw value).</summary>
    public string? ConnectionStringSource
    {
        get => this.connectionStringSource;
        set => this.SetValidatedProperty(ref this.connectionStringSource, value);
    }

    /// <summary>Database name.</summary>
    public string? DatabaseName
    {
        get => this.databaseName;
        set => this.SetValidatedProperty(ref this.databaseName, value);
    }

    /// <summary>Root collection name.</summary>
    public string? RootCollectionName
    {
        get => this.rootCollectionName;
        set => this.SetValidatedProperty(ref this.rootCollectionName, value);
    }

    /// <inheritdoc />
    public override string Description =>
        "Connect to an existing MongoDB you host. Requires a connection string.";

    /// <inheritdoc />
    public override string? ValidationMessage =>
        string.IsNullOrWhiteSpace(this.ConnectionStringSource)
            ? "RemoteMongo requires a connection string."
            : null;

    /// <inheritdoc />
    public override DataAccessConnectionProfile ToProfile() => new()
    {
        Mode = this.Mode,
        MongoConnectionStringSource = this.ConnectionStringSource,
        MongoDatabaseName = this.DatabaseName,
        MongoRootCollectionName = this.RootCollectionName,
    };
}

/// <summary>Settings for the remote web endpoint connection mode.</summary>
public sealed class WebSettingsViewModel : RepositoryConnectionModeViewModel
{
    private string? endpoint;

    /// <summary>Creates the view model from an existing profile.</summary>
    public WebSettingsViewModel(DataAccessConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        this.endpoint = profile.WebEndpoint;
    }

    /// <inheritdoc />
    public override DataAccessMode Mode => DataAccessMode.Web;

    /// <summary>Absolute web endpoint URL.</summary>
    public string? Endpoint
    {
        get => this.endpoint;
        set => this.SetValidatedProperty(ref this.endpoint, value);
    }

    /// <inheritdoc />
    public override string Description =>
        "Connect to a remote Phantom.Workspaces data-access endpoint via HTTPS.";

    /// <inheritdoc />
    public override string? ValidationMessage =>
        Uri.TryCreate(this.Endpoint, UriKind.Absolute, out _)
            ? null
            : "Web requires a valid Endpoint URL.";

    /// <inheritdoc />
    public override DataAccessConnectionProfile ToProfile() => new()
    {
        Mode = this.Mode,
        WebEndpoint = this.Endpoint,
    };
}

/// <summary>Settings for the dev tunnel web endpoint connection mode.</summary>
public sealed class DevTunnelWebSettingsViewModel : RepositoryConnectionModeViewModel
{
    private string? endpoint;

    /// <summary>Creates the view model from an existing profile.</summary>
    public DevTunnelWebSettingsViewModel(DataAccessConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        this.endpoint = profile.WebEndpoint;
    }

    /// <inheritdoc />
    public override DataAccessMode Mode => DataAccessMode.DevTunnelWeb;

    /// <summary>Absolute dev tunnel endpoint URL.</summary>
    public string? Endpoint
    {
        get => this.endpoint;
        set => this.SetValidatedProperty(ref this.endpoint, value);
    }

    /// <inheritdoc />
    public override string Description =>
        "Connect to a remote Phantom.Workspaces endpoint through a dev tunnel. Authorizes with your GitHub auth token (GITHUB_TOKEN or 'gh auth token').";

    /// <inheritdoc />
    public override string? ValidationMessage =>
        Uri.TryCreate(this.Endpoint, UriKind.Absolute, out _)
            ? null
            : "DevTunnelWeb requires a valid Endpoint URL.";

    /// <inheritdoc />
    public override DataAccessConnectionProfile ToProfile() => new()
    {
        Mode = this.Mode,
        WebEndpoint = this.Endpoint,
    };
}
