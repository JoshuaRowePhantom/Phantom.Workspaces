using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentSchema;

namespace Phantom.Workspaces.Llm.Secrets;

/// <summary>
/// Walks manifest-owned string leaves of an <see cref="AgentDefinition"/> to discover <c>${SECRET:Name}</c>
/// placeholder uses, and rewrites those placeholders to opaque per-materialization reference tokens
/// (<c>${SECRET:&lt;handle&gt;}</c>). This type only ever touches placeholder <em>text</em>; it never
/// substitutes a secret value. MCP OAuth client-secret references remain provider-owned metadata
/// for the independently authorized nested credential request.
/// </summary>
public sealed partial class SecretUsageScanner
{
    private const string RootPath = "definition";

    [GeneratedRegex(@"\$\{SECRET:([^}]+)\}")]
    private static partial Regex SecretPlaceholderRegex();

    /// <summary>
    /// Returns every manifest-owned <c>${SECRET:Name}</c> use in <paramref name="definition"/>, in document order.
    /// A single string leaf containing multiple placeholders yields one <see cref="SecretUsage"/> per
    /// occurrence (all sharing the same <see cref="SecretUsage.JsonPath"/>).
    /// </summary>
    public IReadOnlyList<SecretUsage> Scan(AgentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var root = JsonNode.Parse(definition.ToJson());
        var results = new List<SecretUsage>();
        ScanNode(root, RootPath, results);
        return results;
    }

    /// <summary>
    /// Substitutes each <c>${SECRET:&lt;originalName&gt;}</c> in <paramref name="definition"/> with
    /// <c>${SECRET:&lt;handleToken&gt;}</c>, where the token is looked up from
    /// <paramref name="usageToHandleToken"/> keyed by the original <see cref="SecretUsage"/>. The map
    /// values are opaque handle strings; a secret value is never introduced.
    /// </summary>
    public void RewritePlaceholders(
        AgentDefinition definition,
        IReadOnlyDictionary<SecretUsage, string> usageToHandleToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(usageToHandleToken);

        var occurrenceHandles = usageToHandleToken
            .GroupBy(pair => (pair.Key.SecretName, pair.Key.JsonPath))
            .ToDictionary(group => group.Key, group => new Queue<string>(group.Select(pair => pair.Value)));
        var root = JsonNode.Parse(definition.ToJson());
        RewriteNode(root, RootPath, occurrenceHandles);

        var rewritten = PhantomAgentSchema.AgentDefinitionFromJson(root!.ToJsonString())
            ?? throw new InvalidOperationException("Failed to re-parse the rewritten agent definition.");

        CopyState(rewritten, definition);
    }

    /// <summary>
    /// Partitions the discovered secret uses by their dependency owner (issue #1672): uses that
    /// belong to a top-level MCP server's <c>connection</c> (its stored-secret credential material)
    /// are returned as <see cref="SecretUsagePartition.DeferredMcpServer"/> so their acquisition can
    /// be deferred to that server's own connection boundary; every other use — model credentials,
    /// system prompt, and non-MCP tool text — is <see cref="SecretUsagePartition.RequiredCore"/> and
    /// must be materialized fail-closed before the session is created. Provider-owned OAuth
    /// client-secrets are excluded from scanning entirely and therefore appear in neither partition.
    /// </summary>
    public SecretUsagePartition PartitionByOwner(AgentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var usages = this.Scan(definition);
        if (usages.Count == 0)
        {
            return new SecretUsagePartition([], []);
        }

        var root = JsonNode.Parse(definition.ToJson());
        var requiredCore = new List<SecretUsage>();
        var deferredMcpServer = new List<SecretUsage>();
        foreach (var usage in usages)
        {
            if (IsMcpServerConnectionOwned(root, usage.JsonPath))
            {
                deferredMcpServer.Add(usage);
            }
            else
            {
                requiredCore.Add(usage);
            }
        }

        return new SecretUsagePartition(requiredCore, deferredMcpServer);
    }

    // A usage is owned by an MCP server when its JSON path lies within the `connection` subtree of a
    // top-level tool whose kind is "mcp". The AgentSchema serializer emits `tools` as an object keyed
    // by tool name (e.g. definition.tools.<name>.connection.apiKey); an array form is also handled
    // defensively. Scoping ownership to the `connection` subtree targets the credential material the
    // stored-secret/OAuth MCP transports resolve, leaving prompt-shaped or descriptive tool text in
    // the required-core partition.
    private static bool IsMcpServerConnectionOwned(JsonNode? root, string jsonPath)
    {
        if (root is not JsonObject agent || agent["tools"] is not { } toolsNode)
        {
            return false;
        }

        switch (toolsNode)
        {
            case JsonObject toolsObject:
                foreach (var (name, toolNode) in toolsObject)
                {
                    if (PathIsWithinConnection($"{RootPath}.tools.{name}", jsonPath) && IsMcpTool(toolNode))
                    {
                        return true;
                    }
                }

                break;

            case JsonArray toolsArray:
                for (var index = 0; index < toolsArray.Count; index++)
                {
                    if (PathIsWithinConnection($"{RootPath}.tools[{index}]", jsonPath) && IsMcpTool(toolsArray[index]))
                    {
                        return true;
                    }
                }

                break;
        }

        return false;
    }

    private static bool PathIsWithinConnection(string toolPath, string jsonPath)
    {
        var connectionPath = $"{toolPath}.connection";
        return jsonPath == connectionPath
            || jsonPath.StartsWith($"{connectionPath}.", StringComparison.Ordinal)
            || jsonPath.StartsWith($"{connectionPath}[", StringComparison.Ordinal);
    }

    private static bool IsMcpTool(JsonNode? toolNode)
        => toolNode is JsonObject tool
            && tool["kind"] is JsonValue kind
            && kind.TryGetValue<string>(out var kindName)
            && kindName == "mcp";

    private static void ScanNode(JsonNode? node, string path, List<SecretUsage> results)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj)
                {
                    if (!IsProviderOwnedOAuthClientSecret(obj, property.Key))
                    {
                        ScanNode(property.Value, $"{path}.{property.Key}", results);
                    }
                }

                break;

            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    ScanNode(array[index], $"{path}[{index}]", results);
                }

                break;

            case JsonValue value when value.TryGetValue<string>(out var text):
                foreach (Match match in SecretPlaceholderRegex().Matches(text))
                {
                    results.Add(new SecretUsage(match.Groups[1].Value, path));
                }

                break;
        }
    }

    private static void RewriteNode(
        JsonNode? node,
        string path,
        IReadOnlyDictionary<(string SecretName, string JsonPath), Queue<string>> usageToHandleToken)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(property => property.Key).ToArray())
                {
                    if (IsProviderOwnedOAuthClientSecret(obj, key))
                    {
                        continue;
                    }
                    var childPath = $"{path}.{key}";
                    if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        obj[key] = Rewrite(text, childPath, usageToHandleToken);
                    }
                    else
                    {
                        RewriteNode(obj[key], childPath, usageToHandleToken);
                    }
                }

                break;

            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    var childPath = $"{path}[{index}]";
                    if (array[index] is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        array[index] = Rewrite(text, childPath, usageToHandleToken);
                    }
                    else
                    {
                        RewriteNode(array[index], childPath, usageToHandleToken);
                    }
                }

                break;
        }
    }

    private static bool IsProviderOwnedOAuthClientSecret(JsonObject connection, string propertyName)
        => propertyName == "clientSecret"
            && connection["kind"] is JsonValue connectionKind
            && connectionKind.TryGetValue<string>(out var kind)
            && kind == "oauth"
            && connection.Parent is JsonObject tool
            && ReferenceEquals(tool["connection"], connection)
            && tool["kind"] is JsonValue toolKind
            && toolKind.TryGetValue<string>(out var toolKindName)
            && toolKindName == "mcp"
            && tool.Parent is JsonObject tools
            && tools.Parent is JsonObject agent
            && agent.Parent is null
            && ReferenceEquals(agent["tools"], tools)
            && agent["kind"] is JsonValue agentKind
            && agentKind.TryGetValue<string>(out var agentKindName)
            && agentKindName == "prompt";

    private static string Rewrite(
        string text,
        string path,
        IReadOnlyDictionary<(string SecretName, string JsonPath), Queue<string>> usageToHandleToken)
    {
        return SecretPlaceholderRegex().Replace(text, match =>
        {
            var usage = (match.Groups[1].Value, path);
            return usageToHandleToken.TryGetValue(usage, out var handles) && handles.TryDequeue(out var handle)
                ? $"${{SECRET:{handle}}}"
                : match.Value;
        });
    }

    private static void CopyState(AgentDefinition source, AgentDefinition destination)
    {
        var type = destination.GetType();
        if (source.GetType() != type)
        {
            throw new InvalidOperationException(
                "Rewritten agent definition changed concrete type; cannot copy state.");
        }

        foreach (var property in type.GetProperties())
        {
            if (property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
            {
                property.SetValue(destination, property.GetValue(source));
            }
        }
    }
}
