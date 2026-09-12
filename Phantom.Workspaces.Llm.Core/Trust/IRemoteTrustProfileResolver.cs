namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// Result of resolving a stored trust profile on the launch host (issue #1477).
/// </summary>
public sealed record RemoteTrustProfileResolution(TrustProfile Profile, string? Revision);

/// <summary>
/// Resolves a stored trust-profile reference to its effective <see cref="TrustProfile"/> on the
/// launch host, together with the entity revision the resolver observed. Used by
/// <c>RemoteMcpHostHandler</c> (issue #1477) to compile policy locally and to reject launches
/// whose caller-observed revision no longer matches the resolved profile.
/// </summary>
public interface IRemoteTrustProfileResolver
{
    /// <summary>
    /// Resolve, compose and return the effective profile plus its current revision, or null when
    /// no profile matches.
    /// </summary>
    Task<RemoteTrustProfileResolution?> ResolveAsync(string profileReference, CancellationToken cancellationToken);
}
