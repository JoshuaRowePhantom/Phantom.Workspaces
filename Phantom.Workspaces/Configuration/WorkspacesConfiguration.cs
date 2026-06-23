using System;
using System.Collections.Generic;

namespace Phantom.Workspaces.Configuration;

/// <summary>
/// The repository data-access mode chosen during installation/configuration.
/// </summary>
public enum DataAccessMode
{
    /// <summary>Local MongoDB running in a Docker Desktop container.</summary>
    LocalMongoContainer = 0,

    /// <summary>A remote MongoDB connection.</summary>
    RemoteMongo = 1,

    /// <summary>A remote Phantom.Workspaces web data-access endpoint.</summary>
    Web = 2,

    /// <summary>A remote Phantom.Workspaces web endpoint reached through a dev tunnel.</summary>
    DevTunnelWeb = 3,
}

/// <summary>
/// The access mode used when hosting a dev tunnel for remote web access.
/// </summary>
public enum DevTunnelAccessMode
{
    /// <summary>Private tunnel requiring authenticated identity (default).</summary>
    Private = 0,

    /// <summary>Non-interactive access via a tunnel access token.</summary>
    Token = 1,

    /// <summary>Anonymous access (opt-in; should be warned in UI).</summary>
    Anonymous = 2,
}

/// <summary>
/// The data-access connection profile portion of <see cref="WorkspacesConfiguration"/>.
/// </summary>
/// <remarks>
/// Secret material is never stored directly. Token / connection-string values are referenced
/// by source name (for example, an environment variable name), resolved at runtime.
/// </remarks>
public sealed record DataAccessConnectionProfile
{
    /// <summary>The selected data-access mode.</summary>
    public DataAccessMode Mode { get; init; } = DataAccessMode.LocalMongoContainer;

    /// <summary>Container name for <see cref="DataAccessMode.LocalMongoContainer"/>.</summary>
    public string? MongoContainerName { get; init; }

    /// <summary>Host data directory mapped into the MongoDB container.</summary>
    public string? MongoDataDirectory { get; init; }

    /// <summary>MongoDB database name.</summary>
    public string? MongoDatabaseName { get; init; }

    /// <summary>Root collection name for entity storage.</summary>
    public string? MongoRootCollectionName { get; init; }

    /// <summary>Host port the MongoDB container is published on.</summary>
    public int? MongoHostPort { get; init; }

    /// <summary>
    /// Name of the source (for example, an environment variable) that supplies the remote
    /// MongoDB connection string for <see cref="DataAccessMode.RemoteMongo"/>. Never the raw
    /// connection string itself.
    /// </summary>
    public string? MongoConnectionStringSource { get; init; }

    /// <summary>Absolute web endpoint URL for web / dev tunnel modes.</summary>
    public string? WebEndpoint { get; init; }
}

/// <summary>
/// Settings controlling whether this instance exposes a web data-access endpoint.
/// </summary>
public sealed record RemoteHostingSettings
{
    /// <summary>Whether remote hosting (the web DAL endpoint) is enabled.</summary>
    public bool Enabled { get; init; }

    /// <summary>The URL the web server binds to when hosting is enabled.</summary>
    public string ListenUrl { get; init; } = "http://localhost:5280";

    /// <summary>
    /// Whether this instance accepts reverse-direction trusted execution from instances it connects
    /// out to (any authenticated peer over the established tunnel). Off by default; opt-in.
    /// </summary>
    public bool AcceptReverseExecution { get; init; }
}

/// <summary>
/// Dev tunnel host configuration. Token material is referenced by source, never stored raw.
/// </summary>
public sealed record DevTunnelConfiguration
{
    /// <summary>Persistent tunnel id, when one is allocated.</summary>
    public string? TunnelId { get; init; }

    /// <summary>Friendly tunnel name.</summary>
    public string? TunnelName { get; init; }

    /// <summary>Ports hosted through the tunnel.</summary>
    public IReadOnlyList<int> HostedPorts { get; init; } = [];

    /// <summary>Protocol metadata for the hosted ports.</summary>
    public string Protocol { get; init; } = "https";

    /// <summary>Tunnel access mode; private by default.</summary>
    public DevTunnelAccessMode AccessMode { get; init; } = DevTunnelAccessMode.Private;

    /// <summary>
    /// Name of the source that supplies the tunnel access token for non-interactive access.
    /// Never the raw token value.
    /// </summary>
    public string? AccessTokenSource { get; init; }
}

/// <summary>Visual / application preferences.</summary>
public sealed record VisualSettings
{
    /// <summary>The selected theme variant.</summary>
    public string Theme { get; init; } = "Fluent";
}

/// <summary>
/// Root persisted configuration model for installation and runtime settings.
/// </summary>
public sealed record WorkspacesConfiguration
{
    /// <summary>Schema version of this configuration document.</summary>
    public int Version { get; init; } = 1;

    /// <summary>The repository data-access connection profile.</summary>
    public DataAccessConnectionProfile DataAccess { get; init; } = new();

    /// <summary>Remote-hosting (web DAL endpoint) settings.</summary>
    public RemoteHostingSettings RemoteHosting { get; init; } = new();

    /// <summary>Dev tunnel host configuration.</summary>
    public DevTunnelConfiguration DevTunnel { get; init; } = new();

    /// <summary>Visual / application preferences.</summary>
    public VisualSettings Visual { get; init; } = new();

    /// <summary>
    /// Testing only: overrides the computer identity used when composing this instance's
    /// user-computer-profile entity name, so multiple instances can run on one machine with distinct
    /// profiles (and therefore distinct dev tunnels, MCP-server namespaces, and session areas).
    /// Null/empty uses the real host name. Not for production use.
    /// </summary>
    public string? UserComputerProfileOverride { get; init; }

    /// <summary>
    /// Projects the configured data-access profile into a <see cref="RepositorySource"/>
    /// consumable by <see cref="EntityRepository"/>.
    /// </summary>
    public RepositorySource ToRepositorySource()
    {
        return this.DataAccess.Mode switch
        {
            DataAccessMode.LocalMongoContainer => new MongoDbRepositorySource(
                ContainerName: this.DataAccess.MongoContainerName ?? string.Empty,
                RootCollectionName: this.DataAccess.MongoRootCollectionName ?? string.Empty,
                DataDirectory: this.DataAccess.MongoDataDirectory,
                DatabaseName: this.DataAccess.MongoDatabaseName,
                HostPort: this.DataAccess.MongoHostPort),
            DataAccessMode.Web => new WebRepositorySource(
                this.DataAccess.WebEndpoint
                    ?? throw new InvalidOperationException(
                        "Web data-access mode requires a web endpoint URL.")),
            DataAccessMode.DevTunnelWeb => this.DataAccess.WebEndpoint is { Length: > 0 } devTunnelEndpoint
                ? new WebRepositorySource(devTunnelEndpoint, UseGitHubAuthToken: true)
                : this.DevTunnel.TunnelName is { Length: > 0 } devTunnelName
                    ? new DevTunnelNameRepositorySource(
                        devTunnelName,
                        this.DevTunnel.AccessMode,
                        this.DevTunnel.AccessTokenSource)
                    : throw new InvalidOperationException(
                        "Dev tunnel web data-access mode requires either a web endpoint URL or a dev tunnel name."),
            DataAccessMode.RemoteMongo => throw new InvalidOperationException(
                "Remote MongoDB connection is not yet supported by RepositorySource."),
            _ => throw new InvalidOperationException(
                $"Unsupported data-access mode: {this.DataAccess.Mode}."),
        };
    }
}
