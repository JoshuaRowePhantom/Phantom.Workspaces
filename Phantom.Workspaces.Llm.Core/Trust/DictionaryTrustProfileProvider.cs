namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// An <see cref="ITrustProfileProvider"/> backed by an in-memory dictionary of parsed
/// <see cref="TrustProfileEntity"/> values keyed by name.
/// </summary>
/// <remarks>
/// Base profiles are flattened depth-first (base-most first) with cycle detection, then composed
/// restrictively by <see cref="TrustProfileComposer"/>. An entity data-access-backed provider can
/// build the dictionary from a query and delegate to this type.
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

        var ordered = new List<TrustProfileDefinition>();
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var resolved = new HashSet<string>(StringComparer.Ordinal);
        this.Flatten(profileName, ordered, visiting, resolved);

        return ValueTask.FromResult(TrustProfileComposer.Compose(ordered));
    }

    private void Flatten(
        string profileName,
        List<TrustProfileDefinition> ordered,
        HashSet<string> visiting,
        HashSet<string> resolved)
    {
        if (resolved.Contains(profileName))
        {
            return;
        }

        if (!visiting.Add(profileName))
        {
            throw new InvalidOperationException(
                $"Cycle detected in trust profile inheritance involving '{profileName}'.");
        }

        if (!this.entitiesByName.TryGetValue(profileName, out var entity))
        {
            throw new InvalidOperationException($"Trust profile '{profileName}' could not be resolved.");
        }

        foreach (var baseName in entity.BaseTrustProfileNames)
        {
            this.Flatten(baseName, ordered, visiting, resolved);
        }

        ordered.Add(entity.Definition);
        visiting.Remove(profileName);
        resolved.Add(profileName);
    }
}
