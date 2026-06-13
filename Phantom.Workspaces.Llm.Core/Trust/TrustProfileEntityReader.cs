using System.Text.Json;
using System.Text.Json.Nodes;

namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// An entity-level trust profile parsed from a persisted <c>llm-trust-profile</c> entity,
/// including its base-profile references prior to composition.
/// </summary>
public sealed record TrustProfileEntity
{
    /// <summary>Optional simple lookup name for the profile.</summary>
    public string? Name { get; init; }

    /// <summary>Names/ids of base profiles this profile inherits from.</summary>
    public IReadOnlyList<string> BaseTrustProfileNames { get; init; } = [];

    /// <summary>The policy carried by this profile (excluding inheritance).</summary>
    public TrustProfileDefinition Definition { get; init; } = new();
}

/// <summary>
/// Parses persisted <c>llm-trust-profile</c> entity JSON into a <see cref="TrustProfileEntity"/>.
/// </summary>
public static class TrustProfileEntityReader
{
    /// <summary>Reads a trust profile entity from its JSON element.</summary>
    public static TrustProfileEntity Read(JsonElement entity)
    {
        if (entity.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("A trust profile entity must be a JSON object.");
        }

        return new TrustProfileEntity
        {
            Name = ReadName(entity),
            BaseTrustProfileNames = ReadStringArray(entity, "base-trust-profiles"),
            Definition = new TrustProfileDefinition
            {
                HostingWorkspacesClientInstances = ReadStringArray(entity, "hosting-workspaces-client-instances"),
                NetworkAccessPolicy = ReadNetworkAccessPolicy(entity),
                MountPoints = ReadMountPoints(entity),
                HttpsProxyPolicy = ReadHttpsProxyPolicy(entity),
                AllowedMcpToolCallSchemas = ReadSchemas(entity),
            },
        };
    }

    private static string? ReadName(JsonElement entity)
    {
        if (!entity.TryGetProperty("names", out var names) || names.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var name in names.EnumerateArray())
        {
            // Entity names are component arrays; use the last component as the simple name.
            if (name.ValueKind == JsonValueKind.Array && name.GetArrayLength() > 0)
            {
                var last = name[name.GetArrayLength() - 1];
                if (last.ValueKind == JsonValueKind.String)
                {
                    return last.GetString();
                }
            }
        }

        return null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement entity, string propertyName)
    {
        if (!entity.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<string>();
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                var value = element.GetString();
                if (!string.IsNullOrEmpty(value))
                {
                    values.Add(value);
                }
            }
        }

        return values;
    }

    private static TrustNetworkAccessPolicy ReadNetworkAccessPolicy(JsonElement entity)
    {
        if (!entity.TryGetProperty("network-access-policy", out var policy) || policy.ValueKind != JsonValueKind.String)
        {
            return TrustNetworkAccessPolicy.NoNetwork;
        }

        return policy.GetString() switch
        {
            "no-network" => TrustNetworkAccessPolicy.NoNetwork,
            "local-network" => TrustNetworkAccessPolicy.LocalNetwork,
            "natted-network" => TrustNetworkAccessPolicy.NattedNetwork,
            "host-network" => TrustNetworkAccessPolicy.HostNetwork,
            var other => throw new InvalidOperationException($"Unknown network access policy: '{other}'."),
        };
    }

    private static IReadOnlyList<TrustMountPoint> ReadMountPoints(JsonElement entity)
    {
        if (!entity.TryGetProperty("mount-points", out var mounts) || mounts.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var mountPoints = new List<TrustMountPoint>();
        foreach (var mount in mounts.EnumerateArray())
        {
            mountPoints.Add(new TrustMountPoint(
                ReadRequiredString(mount, "source-path"),
                ReadRequiredString(mount, "target-path"),
                ReadAccessMode(mount),
                ReadMountType(mount)));
        }

        return mountPoints;
    }

    private static TrustMountAccessMode ReadAccessMode(JsonElement mount)
    {
        return ReadRequiredString(mount, "access-mode") switch
        {
            "read-only" => TrustMountAccessMode.ReadOnly,
            "read-write" => TrustMountAccessMode.ReadWrite,
            var other => throw new InvalidOperationException($"Unknown mount access mode: '{other}'."),
        };
    }

    private static TrustMountType ReadMountType(JsonElement mount)
    {
        return ReadRequiredString(mount, "type") switch
        {
            "bind" => TrustMountType.Bind,
            "volume" => TrustMountType.Volume,
            "tmpfs" => TrustMountType.Tmpfs,
            var other => throw new InvalidOperationException($"Unknown mount type: '{other}'."),
        };
    }

    private static TrustHttpsProxyPolicy ReadHttpsProxyPolicy(JsonElement entity)
    {
        if (!entity.TryGetProperty("https-proxy-policy", out var policy) || policy.ValueKind != JsonValueKind.Object)
        {
            return TrustHttpsProxyPolicy.Disabled;
        }

        var mode = ReadRequiredString(policy, "mode") switch
        {
            "disabled" => TrustHttpsProxyMode.Disabled,
            "optional" => TrustHttpsProxyMode.Optional,
            "required" => TrustHttpsProxyMode.Required,
            var other => throw new InvalidOperationException($"Unknown HTTPS proxy mode: '{other}'."),
        };

        var proxyUrl = policy.TryGetProperty("proxy-url", out var proxyUrlElement)
            && proxyUrlElement.ValueKind == JsonValueKind.String
            ? proxyUrlElement.GetString()
            : null;

        var credentialsReference = policy.TryGetProperty("credentials-reference", out var credentialsElement)
            && credentialsElement.ValueKind == JsonValueKind.String
            ? credentialsElement.GetString()
            : null;

        return new TrustHttpsProxyPolicy(mode, proxyUrl, credentialsReference);
    }

    private static IReadOnlyList<JsonObject> ReadSchemas(JsonElement entity)
    {
        if (!entity.TryGetProperty("allowed-mcp-tool-call-schemas", out var schemas) || schemas.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<JsonObject>();
        foreach (var schema in schemas.EnumerateArray())
        {
            if (JsonNode.Parse(schema.GetRawText()) is JsonObject schemaObject)
            {
                result.Add(schemaObject);
            }
        }

        return result;
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"Trust profile property '{propertyName}' must be a string.");
        }

        return value.GetString()!;
    }
}
