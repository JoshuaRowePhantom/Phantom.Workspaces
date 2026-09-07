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

    /// <summary>Base profile references this profile inherits from, each with an inheritance mode.</summary>
    public IReadOnlyList<TrustProfileBaseReference> Bases { get; init; } = [];

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

        RejectRemovedProperty(entity, "mount-points");
        RejectRemovedProperty(entity, "network-access-policy");

        return new TrustProfileEntity
        {
            Name = ReadName(entity),
            Bases = ReadBaseReferences(entity),
            Definition = new TrustProfileDefinition
            {
                HostingWorkspacesClientInstances = ReadStringArray(entity, "hosting-workspaces-client-instances"),
                NetworkCapabilities = ReadNetworkCapabilities(entity),
                FilesystemPaths = ReadFilesystemPaths(entity),
                DataSharing = ReadDataSharing(entity),
                DefaultExecutionTarget = ReadOptionalObject(entity, "default-execution-target"),
                HttpsProxyPolicy = ReadHttpsProxyPolicy(entity),
                AllowedMcpToolCallSchemas = ReadSchemas(entity, "allowed-mcp-tool-call-schemas"),
                RestrictedMcpToolCallSchemas = ReadSchemas(entity, "restricted-mcp-tool-call-schemas"),
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

    private static IReadOnlyList<TrustProfileBaseReference> ReadBaseReferences(JsonElement entity)
    {
        if (!entity.TryGetProperty("base-trust-profiles", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var references = new List<TrustProfileBaseReference>();
        foreach (var element in array.EnumerateArray())
        {
            switch (element.ValueKind)
            {
                // Back-compatible: a bare string reference inherits restrictively.
                case JsonValueKind.String when !string.IsNullOrEmpty(element.GetString()):
                    references.Add(new TrustProfileBaseReference(element.GetString()!, TrustInheritanceMode.Restrictive));
                    break;
                case JsonValueKind.Object:
                    var profile = element.TryGetProperty("profile", out var profileElement)
                        && profileElement.ValueKind == JsonValueKind.String
                        ? profileElement.GetString()
                        : null;
                    if (string.IsNullOrEmpty(profile))
                    {
                        throw new InvalidOperationException("A base trust profile reference must include a 'profile' name.");
                    }

                    references.Add(new TrustProfileBaseReference(profile, ReadInheritanceMode(element)));
                    break;
            }
        }

        return references;
    }

    private static TrustInheritanceMode ReadInheritanceMode(JsonElement element)
    {
        if (!element.TryGetProperty("inheritance-mode", out var modeElement) || modeElement.ValueKind != JsonValueKind.String)
        {
            return TrustInheritanceMode.Restrictive;
        }

        return modeElement.GetString() switch
        {
            "restrictive" => TrustInheritanceMode.Restrictive,
            "permissive" => TrustInheritanceMode.Permissive,
            var other => throw new InvalidOperationException($"Unknown inheritance mode: '{other}'."),
        };
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

    private static IReadOnlyList<string>? ReadNetworkCapabilities(JsonElement entity)
    {
        if (!entity.TryGetProperty("network-capabilities", out var capabilities))
        {
            return null;
        }

        if (capabilities.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Trust profile property 'network-capabilities' must be an array.");
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in capabilities.EnumerateArray())
        {
            if (capability.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(capability.GetString()))
            {
                throw new InvalidOperationException("Network capabilities must be non-blank strings.");
            }

            var value = capability.GetString()!;
            if (!seen.Add(value))
            {
                throw new InvalidOperationException($"Duplicate network capability: '{value}'.");
            }

            result.Add(value);
        }

        return result;
    }

    private static IReadOnlyList<TrustFilesystemPath> ReadFilesystemPaths(JsonElement entity)
    {
        if (!entity.TryGetProperty("filesystem-paths", out var paths))
        {
            return [];
        }

        if (paths.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Trust profile property 'filesystem-paths' must be an array.");
        }

        var result = new List<TrustFilesystemPath>();
        foreach (var path in paths.EnumerateArray())
        {
            if (path.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("Each filesystem path must be an object.");
            }

            foreach (var property in path.EnumerateObject())
            {
                if (property.Name is not ("source-path" or "target-path" or "access-mode"))
                {
                    throw new InvalidOperationException($"Unknown filesystem path property: '{property.Name}'.");
                }
            }

            result.Add(new TrustFilesystemPath(
                ReadRequiredString(path, "source-path"),
                ReadOptionalString(path, "target-path"),
                ReadAccessMode(path)));
        }

        return result;
    }

    private static JsonElement? ReadOptionalObject(JsonElement entity, string propertyName)
    {
        if (!entity.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Trust profile property '{propertyName}' must be an object.");
        }

        return value.Clone();
    }

    private static TrustFilesystemAccessMode ReadAccessMode(JsonElement path)
    {
        return ReadRequiredString(path, "access-mode") switch
        {
            "read-only" => TrustFilesystemAccessMode.ReadOnly,
            "read-write" => TrustFilesystemAccessMode.ReadWrite,
            var other => throw new InvalidOperationException($"Unknown filesystem access mode: '{other}'."),
        };
    }

    private static TrustDataSharing ReadDataSharing(JsonElement entity)
    {
        if (!entity.TryGetProperty("data-sharing", out var sharing))
        {
            return TrustDataSharing.Full;
        }

        if (sharing.ValueKind == JsonValueKind.String)
        {
            return sharing.GetString() switch
            {
                "full" => TrustDataSharing.Full,
                "none" => TrustDataSharing.None,
                var other => throw new InvalidOperationException($"Unknown data-sharing mode: '{other}'."),
            };
        }

        if (sharing.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Data-sharing must be 'full', 'none', or a regime object.");
        }

        var properties = sharing.EnumerateObject().ToList();
        if (properties.Count != 1 || properties[0].Name != "regime")
        {
            throw new InvalidOperationException("A data-sharing regime object must contain only 'regime'.");
        }

        return TrustDataSharing.Regime(ReadRequiredString(sharing, "regime"));
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

    private static IReadOnlyList<JsonObject> ReadSchemas(JsonElement entity, string propertyName)
    {
        if (!entity.TryGetProperty(propertyName, out var schemas) || schemas.ValueKind != JsonValueKind.Array)
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

        var result = value.GetString();
        if (string.IsNullOrWhiteSpace(result))
        {
            throw new InvalidOperationException($"Trust profile property '{propertyName}' must not be blank.");
        }

        return result;
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidOperationException($"Trust profile property '{propertyName}' must be a non-blank string.");
        }

        return value.GetString();
    }

    private static void RejectRemovedProperty(JsonElement entity, string propertyName)
    {
        if (entity.TryGetProperty(propertyName, out _))
        {
            throw new InvalidOperationException($"Trust profile property '{propertyName}' has been removed.");
        }
    }
}
