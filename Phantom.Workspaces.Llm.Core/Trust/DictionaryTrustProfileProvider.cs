namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// An <see cref="ITrustProfileProvider"/> backed by an in-memory dictionary of parsed
/// <see cref="TrustProfileEntity"/> values keyed by name.
/// </summary>
/// <remarks>
/// Each base profile is composed into the profile that inherits it according to its
/// <see cref="TrustInheritanceMode"/> (restrictive narrows, permissive widens), recursively and
/// with cycle detection, via <see cref="TrustProfileComposer"/>.
/// </remarks>
public sealed class DictionaryTrustProfileProvider : ITrustProfileProvider
{
    private readonly IReadOnlyDictionary<string, TrustProfileEntity> entitiesByName;

    /// <summary>Creates a provider over the supplied profiles keyed by name.</summary>
    public DictionaryTrustProfileProvider(IReadOnlyDictionary<string, TrustProfileEntity> entitiesByName)
    {
        ArgumentNullException.ThrowIfNull(entitiesByName);
        this.entitiesByName = entitiesByName;
    }

    /// <inheritdoc />
    public ValueTask<TrustProfile> ResolveAsync(string profileName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);

        var composed = this.ComposeDefinition(profileName, new HashSet<string>(StringComparer.Ordinal));
        return ValueTask.FromResult(TrustProfileComposer.Finalize(composed));
    }

    private TrustProfileDefinition ComposeDefinition(string profileName, HashSet<string> visiting)
    {
        if (!visiting.Add(profileName))
        {
            throw new InvalidOperationException(
                $"Cycle detected in trust profile inheritance involving '{profileName}'.");
        }

        try
        {
            if (!this.entitiesByName.TryGetValue(profileName, out var entity))
            {
                throw new InvalidOperationException($"Trust profile '{profileName}' could not be resolved.");
            }

            var effective = entity.Definition;
            foreach (var baseReference in entity.Bases)
            {
                var baseDefinition = this.ComposeDefinition(baseReference.ProfileName, visiting);
                effective = TrustProfileComposer.Merge(effective, baseDefinition, baseReference.Mode);
            }

            return effective;
        }
        finally
        {
            visiting.Remove(profileName);
        }
    }
}
