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

/// <summary>A composed trust profile and the entity revision from which it was resolved.</summary>
public sealed record VersionedTrustProfile(TrustProfile Profile, string? Revision);

/// <summary>Optional extension for providers that can preserve the selected entity revision.</summary>
public interface IVersionedTrustProfileProvider : ITrustProfileProvider
{
    ValueTask<VersionedTrustProfile> ResolveVersionedAsync(
        string profileName,
        CancellationToken cancellationToken = default);
}
