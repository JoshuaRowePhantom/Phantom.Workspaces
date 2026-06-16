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
/// restrictive policy, mount points intersect (read-only narrowing), and HTTPS proxy takes the
/// strongest requirement.</item>
/// <item><b>Permissive</b> widens: client instances union, network access takes the most permissive
/// policy, mount points union (read-write widening), and HTTPS proxy takes the weakest requirement.</item>
/// </list>
/// MCP tool-call schemas are always composed additively (their <c>anyOf</c> union) in both modes.
/// All merge operations are commutative, so ordering does not affect the result.
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
            NetworkAccessPolicy = mode == TrustInheritanceMode.Restrictive
                ? (TrustNetworkAccessPolicy)Math.Min((int)primary.NetworkAccessPolicy, (int)other.NetworkAccessPolicy)
                : (TrustNetworkAccessPolicy)Math.Max((int)primary.NetworkAccessPolicy, (int)other.NetworkAccessPolicy),
            MountPoints = mode == TrustInheritanceMode.Restrictive
                ? IntersectMounts(primary.MountPoints, other.MountPoints)
                : UnionMounts(primary.MountPoints, other.MountPoints),
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
            NetworkAccessPolicy = definition.NetworkAccessPolicy,
            MountPoints = definition.MountPoints,
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

    private static IReadOnlyList<TrustMountPoint> IntersectMounts(
        IReadOnlyList<TrustMountPoint> primary,
        IReadOnlyList<TrustMountPoint> other)
    {
        var result = new List<TrustMountPoint>();
        foreach (var candidate in primary)
        {
            var match = FindMount(other, candidate);
            if (match is null)
            {
                continue;
            }

            // Restrictive: read-only wins.
            var access = candidate.AccessMode == TrustMountAccessMode.ReadOnly || match.AccessMode == TrustMountAccessMode.ReadOnly
                ? TrustMountAccessMode.ReadOnly
                : TrustMountAccessMode.ReadWrite;
            result.Add(candidate with { AccessMode = access });
        }

        return result;
    }

    private static IReadOnlyList<TrustMountPoint> UnionMounts(
        IReadOnlyList<TrustMountPoint> primary,
        IReadOnlyList<TrustMountPoint> other)
    {
        var result = new List<TrustMountPoint>();
        foreach (var mount in primary)
        {
            var match = FindMount(other, mount);
            // Permissive: read-write wins.
            var access = mount.AccessMode == TrustMountAccessMode.ReadWrite || match?.AccessMode == TrustMountAccessMode.ReadWrite
                ? TrustMountAccessMode.ReadWrite
                : TrustMountAccessMode.ReadOnly;
            result.Add(mount with { AccessMode = access });
        }

        foreach (var mount in other)
        {
            if (FindMount(primary, mount) is null)
            {
                result.Add(mount);
            }
        }

        return result;
    }

    private static TrustMountPoint? FindMount(IReadOnlyList<TrustMountPoint> mounts, TrustMountPoint key)
    {
        foreach (var mount in mounts)
        {
            if (string.Equals(mount.SourcePath, key.SourcePath, StringComparison.Ordinal)
                && string.Equals(mount.TargetPath, key.TargetPath, StringComparison.Ordinal)
                && mount.Type == key.Type)
            {
                return mount;
            }
        }

        return null;
    }

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
