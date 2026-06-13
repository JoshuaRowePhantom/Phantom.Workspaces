using System.Text.Json.Nodes;

namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// Composes one or more entity-level <see cref="TrustProfileDefinition"/> values (a profile and
/// its transitive bases) into a single effective runtime <see cref="TrustProfile"/>.
/// </summary>
/// <remarks>
/// Composition is restrictive: combining profiles can only narrow the effective policy.
/// <list type="bullet">
/// <item>Client instances compose by intersection.</item>
/// <item>Network access composes to the most restrictive policy.</item>
/// <item>Mount points compose by intersection of (source, target, type), with access mode
/// narrowed to read-only when any contributing grant is read-only.</item>
/// <item>HTTPS proxy composes to the strongest requirement (required &gt; optional &gt; disabled).</item>
/// <item>MCP tool-call schemas compose into a single <c>anyOf</c> envelope.</item>
/// </list>
/// The order of the supplied definitions does not affect the result.
/// </remarks>
public static class TrustProfileComposer
{
    /// <summary>
    /// Composes the supplied definitions into an effective runtime trust profile.
    /// </summary>
    /// <param name="definitions">The profile plus its transitive base profiles. Must be non-empty.</param>
    public static TrustProfile Compose(IReadOnlyList<TrustProfileDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        if (definitions.Count == 0)
        {
            throw new ArgumentException("At least one trust profile definition is required.", nameof(definitions));
        }

        return new TrustProfile
        {
            HostingWorkspacesClientInstances = ComposeClientInstances(definitions),
            NetworkAccessPolicy = ComposeNetworkAccessPolicy(definitions),
            MountPoints = ComposeMountPoints(definitions),
            HttpsProxyPolicy = ComposeHttpsProxyPolicy(definitions),
            AllowedMcpToolCallSchema = ComposeMcpToolCallSchema(definitions),
        };
    }

    private static IReadOnlyList<string> ComposeClientInstances(IReadOnlyList<TrustProfileDefinition> definitions)
    {
        var effective = new List<string>(definitions[0].HostingWorkspacesClientInstances);
        for (var index = 1; index < definitions.Count; index++)
        {
            var allowed = new HashSet<string>(definitions[index].HostingWorkspacesClientInstances, StringComparer.Ordinal);
            effective.RemoveAll(instance => !allowed.Contains(instance));
        }

        return effective;
    }

    private static TrustNetworkAccessPolicy ComposeNetworkAccessPolicy(IReadOnlyList<TrustProfileDefinition> definitions)
    {
        var effective = TrustNetworkAccessPolicy.HostNetwork;
        foreach (var definition in definitions)
        {
            if (definition.NetworkAccessPolicy < effective)
            {
                effective = definition.NetworkAccessPolicy;
            }
        }

        return effective;
    }

    private static IReadOnlyList<TrustMountPoint> ComposeMountPoints(IReadOnlyList<TrustProfileDefinition> definitions)
    {
        var effective = new List<TrustMountPoint>();
        foreach (var candidate in definitions[0].MountPoints)
        {
            var narrowestAccess = candidate.AccessMode;
            var presentInAll = true;

            for (var index = 1; index < definitions.Count; index++)
            {
                var match = FindMount(definitions[index].MountPoints, candidate);
                if (match is null)
                {
                    presentInAll = false;
                    break;
                }

                if (match.AccessMode == TrustMountAccessMode.ReadOnly)
                {
                    narrowestAccess = TrustMountAccessMode.ReadOnly;
                }
            }

            if (presentInAll)
            {
                effective.Add(candidate with { AccessMode = narrowestAccess });
            }
        }

        return effective;
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

    private static TrustHttpsProxyPolicy ComposeHttpsProxyPolicy(IReadOnlyList<TrustProfileDefinition> definitions)
    {
        var effective = definitions[0].HttpsProxyPolicy;
        for (var index = 1; index < definitions.Count; index++)
        {
            var candidate = definitions[index].HttpsProxyPolicy;
            if (candidate.Mode > effective.Mode)
            {
                effective = candidate;
            }
        }

        return effective;
    }

    private static JsonObject ComposeMcpToolCallSchema(IReadOnlyList<TrustProfileDefinition> definitions)
    {
        var anyOf = new JsonArray();
        foreach (var definition in definitions)
        {
            foreach (var schema in definition.AllowedMcpToolCallSchemas)
            {
                anyOf.Add(schema.DeepClone());
            }
        }

        if (anyOf.Count == 0)
        {
            // No allowed tool-call schemas: deny everything. "not": {} rejects all instances
            // because the empty schema matches everything.
            return new JsonObject { ["not"] = new JsonObject() };
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("toolName", "input"),
            ["anyOf"] = anyOf,
        };
    }
}
