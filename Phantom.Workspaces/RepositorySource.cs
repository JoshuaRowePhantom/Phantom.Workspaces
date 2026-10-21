namespace Phantom.Workspaces;

/// <summary>
/// Describes where a workspace repository's data comes from. This is a closed hierarchy of
/// concrete sources (<see cref="WebRepositorySource"/>, <see cref="LocalGitRepositorySource"/>,
/// <see cref="MongoDbRepositorySource"/>, <see cref="UnknownRepositorySource"/>); consumers
/// pattern-match on the concrete type. Repository selection is configured only through the
/// configuration file (projected via
/// <see cref="Configuration.WorkspacesConfiguration.ToRepositorySource"/>) or the first-run setup
/// wizard, never through command-line parameters.
/// </summary>
public abstract record RepositorySource;

/// <summary>An unspecified repository source; resolves to an in-memory data access layer.</summary>
public sealed record UnknownRepositorySource : RepositorySource;

/// <summary>A remote Phantom.Workspaces web data-access endpoint.</summary>
/// <param name="Endpoint">The absolute endpoint URL.</param>
/// <param name="UseGitHubAuthToken">
/// When true, private dev-tunnel access acquires a Connect credential through the host access broker.
/// GitHub is the default identity provider unless <paramref name="Authentication"/> selects another
/// provider; identity credentials are never forwarded to the relay.
/// </param>
/// <param name="Authentication">
/// Optional pluggable authentication configuration (issue #1455). Because <see cref="EntityRepository"/>
/// and the chat-persistence store both dispatch on this source, this single field configures data and
/// persistence auth uniformly. Null selects GitHub identity for private tunnel access when
/// <paramref name="UseGitHubAuthToken"/> is set.
/// </param>
public sealed record WebRepositorySource(
    string Endpoint,
    bool UseGitHubAuthToken = false,
    RemoteAuthentication? Authentication = null) : RepositorySource;

/// <summary>
/// A remote Phantom.Workspaces web endpoint reached through a dev tunnel located by name. The relay
/// endpoint (and forwarded port) is discovered at connect time from the tunnel name, so the host can
/// change its listening port without the client reconfiguring.
/// </summary>
/// <param name="TunnelName">The dev tunnel name to resolve.</param>
/// <param name="AccessMode">The relay access mode (Anonymous: no relay token; Private/Token: Connect credential acquired through the broker).</param>
/// <param name="Authentication">
/// Optional pluggable authentication configuration (issue #1455), threaded uniformly to the data and
/// persistence layers. Name discovery always requires an independently broker-authorized Management
/// identity. Null selects GitHub; an explicit github/entra/oauth identity is retained even for anonymous
/// relay access. The anonymous scheme selects tokenless relay access, not anonymous Management:
/// discovery uses an unbound GitHub request whose actual account must be verified by the provider.
/// </param>
public sealed record DevTunnelNameRepositorySource(
    string TunnelName,
    Configuration.DevTunnelAccessMode AccessMode,
    RemoteAuthentication? Authentication = null) : RepositorySource;

/// <summary>A local Git-backed repository at the given path.</summary>
public sealed record LocalGitRepositorySource(string Path) : RepositorySource;

/// <summary>A MongoDB container-backed repository.</summary>
public sealed record MongoDbRepositorySource(
    string ContainerName,
    string RootCollectionName,
    string? DataDirectory = null,
    string? DatabaseName = null,
    int? HostPort = null) : RepositorySource;
