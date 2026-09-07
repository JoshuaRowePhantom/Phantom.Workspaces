using System.Text.Json.Nodes;

namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// Composes entity-level <see cref="TrustProfileDefinition"/> values into a single effective
/// runtime <see cref="TrustProfile"/>, supporting both restrictive and permissive inheritance.
/// </summary>
/// <remarks>
/// A base profile combines with the profile that inherits it according to a
/// <see cref="TrustInheritanceMode"/>:
/// <list type="bullet">
/// <item><b>Restrictive</b> narrows: client instances intersect, network access takes the most
/// restrictive capability set, filesystem paths intersect (read-only narrowing), and HTTPS proxy takes the
/// strongest requirement.</item>
/// <item><b>Permissive</b> widens: client instances union, network access takes the most permissive
/// policy, filesystem paths union (read-write widening), and HTTPS proxy takes the weakest requirement.</item>
/// </list>
/// MCP tool-call schemas are always composed additively (their <c>anyOf</c> union) in both modes.
/// Set-based merge operations are commutative. Equally restrictive data-sharing regimes use the
/// later definition, allowing a derived containment context to select its regime.
/// </remarks>
public static class TrustProfileComposer
{
    /// <summary>
    /// Composes the supplied definitions with restrictive inheritance (back-compatible default).
    /// </summary>
    /// <param name="definitions">The profile plus its transitive base profiles. Must be non-empty.</param>
    public static TrustProfile Compose(IReadOnlyList<TrustProfileDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        if (definitions.Count == 0)
        {
            throw new ArgumentException("At least one trust profile definition is required.", nameof(definitions));
        }

        var merged = definitions[0];
        for (var index = 1; index < definitions.Count; index++)
        {
            merged = Merge(merged, definitions[index], TrustInheritanceMode.Restrictive);
        }

        return Finalize(merged);
    }

    /// <summary>Merges <paramref name="other"/> into <paramref name="primary"/> using the given mode.</summary>
    public static TrustProfileDefinition Merge(
        TrustProfileDefinition primary,
        TrustProfileDefinition other,
        TrustInheritanceMode mode)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(other);

        return new TrustProfileDefinition
        {
            HostingWorkspacesClientInstances = mode == TrustInheritanceMode.Restrictive
                ? IntersectInstances(primary.HostingWorkspacesClientInstances, other.HostingWorkspacesClientInstances)
                : UnionInstances(primary.HostingWorkspacesClientInstances, other.HostingWorkspacesClientInstances),
            NetworkCapabilities = mode == TrustInheritanceMode.Restrictive
                ? IntersectCapabilities(primary.NetworkCapabilities, other.NetworkCapabilities)
                : UnionCapabilities(primary.NetworkCapabilities, other.NetworkCapabilities),
            FilesystemPaths = mode == TrustInheritanceMode.Restrictive
                ? IntersectFilesystemPaths(primary.FilesystemPaths, other.FilesystemPaths)
                : UnionFilesystemPaths(primary.FilesystemPaths, other.FilesystemPaths),
            DataSharing = ComposeDataSharing(primary.DataSharing, other.DataSharing, mode),
            DefaultExecutionTarget = primary.DefaultExecutionTarget?.Clone() ?? other.DefaultExecutionTarget?.Clone(),
            HttpsProxyPolicy = mode == TrustInheritanceMode.Restrictive
                ? StrongerProxy(primary.HttpsProxyPolicy, other.HttpsProxyPolicy)
                : WeakerProxy(primary.HttpsProxyPolicy, other.HttpsProxyPolicy),
            AllowedMcpToolCallSchemas = UnionSchemas(primary.AllowedMcpToolCallSchemas, other.AllowedMcpToolCallSchemas),
            RestrictedMcpToolCallSchemas = UnionSchemas(primary.RestrictedMcpToolCallSchemas, other.RestrictedMcpToolCallSchemas),
        };
    }

    /// <summary>Converts a composed definition into the runtime <see cref="TrustProfile"/>.</summary>
    public static TrustProfile Finalize(TrustProfileDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return new TrustProfile
        {
            HostingWorkspacesClientInstances = definition.HostingWorkspacesClientInstances,
            NetworkCapabilities = definition.NetworkCapabilities,
            FilesystemPaths = definition.FilesystemPaths,
            DataSharing = definition.DataSharing,
            DefaultExecutionTarget = definition.DefaultExecutionTarget?.Clone(),
            HttpsProxyPolicy = definition.HttpsProxyPolicy,
            AllowedMcpToolCallSchema = BuildMcpToolCallSchema(
                definition.AllowedMcpToolCallSchemas,
                definition.RestrictedMcpToolCallSchemas),
        };
    }

    private static IReadOnlyList<string> IntersectInstances(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        // A wildcard means "all instances", so it does not restrict the other side.
        if (a.Contains(TrustProfile.WildcardClientInstance))
        {
            return b.ToList();
        }

        if (b.Contains(TrustProfile.WildcardClientInstance))
        {
            return a.ToList();
        }

        var allowed = new HashSet<string>(b, StringComparer.Ordinal);
        return a.Where(allowed.Contains).ToList();
    }

    private static IReadOnlyList<string> UnionInstances(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        // A wildcard means "all instances", so the union is also "all".
        if (a.Contains(TrustProfile.WildcardClientInstance) || b.Contains(TrustProfile.WildcardClientInstance))
        {
            return [TrustProfile.WildcardClientInstance];
        }

        var result = new List<string>(a);
        var seen = new HashSet<string>(a, StringComparer.Ordinal);
        foreach (var instance in b)
        {
            if (seen.Add(instance))
            {
                result.Add(instance);
            }
        }

        return result;
    }

    private static IReadOnlyList<string>? IntersectCapabilities(
        IReadOnlyList<string>? primary,
        IReadOnlyList<string>? other)
    {
        if (primary is null)
        {
            return NormalizeCapabilities(other);
        }

        if (other is null)
        {
            return NormalizeCapabilities(primary);
        }

        var allowed = new HashSet<string>(other, StringComparer.Ordinal);
        return NormalizeCapabilities(primary.Where(allowed.Contains));
    }

    private static IReadOnlyList<string>? UnionCapabilities(
        IReadOnlyList<string>? primary,
        IReadOnlyList<string>? other)
    {
        if (primary is null || other is null)
        {
            return null;
        }

        return NormalizeCapabilities(primary.Concat(other));
    }

    private static IReadOnlyList<string>? NormalizeCapabilities(IEnumerable<string>? capabilities)
        => capabilities?.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    private static IReadOnlyList<TrustFilesystemPath> IntersectFilesystemPaths(
        IReadOnlyList<TrustFilesystemPath> primary,
        IReadOnlyList<TrustFilesystemPath> other)
    {
        var result = new List<TrustFilesystemPath>();
        foreach (var candidate in primary)
        {
            var match = FindFilesystemPath(other, candidate);
            if (match is null)
            {
                continue;
            }

            // Restrictive: read-only wins.
            var access = candidate.AccessMode == TrustFilesystemAccessMode.ReadOnly || match.AccessMode == TrustFilesystemAccessMode.ReadOnly
                ? TrustFilesystemAccessMode.ReadOnly
                : TrustFilesystemAccessMode.ReadWrite;
            result.Add(candidate with { AccessMode = access });
        }

        return result;
    }

    private static IReadOnlyList<TrustFilesystemPath> UnionFilesystemPaths(
        IReadOnlyList<TrustFilesystemPath> primary,
        IReadOnlyList<TrustFilesystemPath> other)
    {
        var result = new List<TrustFilesystemPath>();
        foreach (var mount in primary)
        {
            var match = FindFilesystemPath(other, mount);
            // Permissive: read-write wins.
            var access = mount.AccessMode == TrustFilesystemAccessMode.ReadWrite || match?.AccessMode == TrustFilesystemAccessMode.ReadWrite
                ? TrustFilesystemAccessMode.ReadWrite
                : TrustFilesystemAccessMode.ReadOnly;
            result.Add(mount with { AccessMode = access });
        }

        foreach (var mount in other)
        {
            if (FindFilesystemPath(primary, mount) is null)
            {
                result.Add(mount);
            }
        }

        return result;
    }

    private static TrustFilesystemPath? FindFilesystemPath(
        IReadOnlyList<TrustFilesystemPath> paths,
        TrustFilesystemPath key)
    {
        foreach (var path in paths)
        {
            if (string.Equals(path.SourcePath, key.SourcePath, StringComparison.Ordinal)
                && string.Equals(path.EffectiveTargetPath, key.EffectiveTargetPath, StringComparison.Ordinal))
            {
                return path;
            }
        }

        return null;
    }

    private static TrustDataSharing ComposeDataSharing(
        TrustDataSharing primary,
        TrustDataSharing other,
        TrustInheritanceMode mode)
    {
        var primaryRank = DataSharingRank(primary);
        var otherRank = DataSharingRank(other);
        return mode == TrustInheritanceMode.Restrictive
            ? otherRank >= primaryRank ? other : primary
            : otherRank <= primaryRank ? other : primary;
    }

    private static int DataSharingRank(TrustDataSharing sharing) => sharing switch
    {
        TrustDataSharing.FullSharing => 0,
        TrustDataSharing.RegimeScoped => 1,
        TrustDataSharing.NoSharing => 2,
        _ => throw new InvalidOperationException($"Unknown data-sharing type: {sharing.GetType().Name}."),
    };

    private static TrustHttpsProxyPolicy StrongerProxy(TrustHttpsProxyPolicy a, TrustHttpsProxyPolicy b)
        => b.Mode > a.Mode ? b : a;

    private static TrustHttpsProxyPolicy WeakerProxy(TrustHttpsProxyPolicy a, TrustHttpsProxyPolicy b)
        => b.Mode < a.Mode ? b : a;

    private static IReadOnlyList<JsonObject> UnionSchemas(IReadOnlyList<JsonObject> a, IReadOnlyList<JsonObject> b)
    {
        var result = new List<JsonObject>(a.Count + b.Count);
        foreach (var schema in a)
        {
            result.Add((JsonObject)schema.DeepClone());
        }

        foreach (var schema in b)
        {
            result.Add((JsonObject)schema.DeepClone());
        }

        return result;
    }

    private static JsonObject BuildMcpToolCallSchema(
        IReadOnlyList<JsonObject> allowedSchemas,
        IReadOnlyList<JsonObject> restrictedSchemas)
    {
        JsonObject allowedEnvelope;
        if (allowedSchemas.Count == 0)
        {
            // No allowed tool-call schemas: deny everything. "not": {} rejects all instances
            // because the empty schema matches everything.
            allowedEnvelope = new JsonObject { ["not"] = new JsonObject() };
        }
        else
        {
            var anyOf = new JsonArray();
            foreach (var schema in allowedSchemas)
            {
                anyOf.Add(schema.DeepClone());
            }

            allowedEnvelope = new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray("toolName", "input"),
                ["anyOf"] = anyOf,
            };
        }

        if (restrictedSchemas.Count == 0)
        {
            return allowedEnvelope;
        }

        // A tool call must satisfy the allowed envelope AND not match any restricted schema.
        var restrictedAnyOf = new JsonArray();
        foreach (var schema in restrictedSchemas)
        {
            restrictedAnyOf.Add(schema.DeepClone());
        }

        return new JsonObject
        {
            ["allOf"] = new JsonArray(
                allowedEnvelope,
                new JsonObject { ["not"] = new JsonObject { ["anyOf"] = restrictedAnyOf } }),
        };
    }
}
