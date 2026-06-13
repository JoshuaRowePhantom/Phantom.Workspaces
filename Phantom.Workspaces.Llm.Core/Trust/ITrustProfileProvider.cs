namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// Resolves a trust profile reference into an effective, composed runtime
/// <see cref="TrustProfile"/>, flattening and composing any inherited base profiles.
/// </summary>
public interface ITrustProfileProvider
{
    /// <summary>
    /// Resolves the named trust profile and composes it with its transitive base profiles.
    /// </summary>
    /// <param name="profileName">The profile name/id to resolve.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    ValueTask<TrustProfile> ResolveAsync(string profileName, CancellationToken cancellationToken = default);
}
