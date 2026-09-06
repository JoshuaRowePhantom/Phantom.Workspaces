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
/// When true (dev tunnel access), the <c>X-Tunnel-Authorization</c> token is resolved automatically
/// from the GitHub auth token (the <c>GITHUB_TOKEN</c> environment variable, else <c>gh auth token</c>),
/// so no token source needs to be configured.
/// </param>
/// <param name="Authentication">
/// Optional pluggable authentication configuration (issue #1455). Because <see cref="EntityRepository"/>
/// and the chat-persistence store both dispatch on this source, this single field configures data and
/// persistence auth uniformly. Null preserves the legacy behaviour selected by <paramref name="UseGitHubAuthToken"/>.
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
/// <param name="AccessMode">The tunnel access mode (Anonymous: no token; Private/Token: connect token fetched automatically from Management API).</param>
/// <param name="Authentication">
/// Optional pluggable authentication configuration (issue #1455), threaded uniformly to the data and
/// persistence layers. Null preserves the legacy behaviour derived from <paramref name="AccessMode"/>.
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
