using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentSchema;

namespace Phantom.Workspaces.Llm.Secrets;

/// <summary>
/// Walks every string leaf of an <see cref="AgentDefinition"/> to discover <c>${SECRET:Name}</c>
/// placeholder uses, and rewrites those placeholders to opaque per-materialization reference tokens
/// (<c>${SECRET:&lt;handle&gt;}</c>). This type only ever touches placeholder <em>text</em>; it never
/// substitutes a secret value.
/// </summary>
public sealed partial class SecretUsageScanner
{
    private const string RootPath = "definition";

    [GeneratedRegex(@"\$\{SECRET:([^}]+)\}")]
    private static partial Regex SecretPlaceholderRegex();

    /// <summary>
    /// Returns every <c>${SECRET:Name}</c> use in <paramref name="definition"/>, in document order.
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

        var root = JsonNode.Parse(definition.ToJson());
        RewriteNode(root, RootPath, usageToHandleToken);

        var rewritten = PhantomAgentSchema.AgentDefinitionFromJson(root!.ToJsonString())
            ?? throw new InvalidOperationException("Failed to re-parse the rewritten agent definition.");

        CopyState(rewritten, definition);
    }

    private static void ScanNode(JsonNode? node, string path, List<SecretUsage> results)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj)
                {
                    ScanNode(property.Value, $"{path}.{property.Key}", results);
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
        IReadOnlyDictionary<SecretUsage, string> usageToHandleToken)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(property => property.Key).ToArray())
                {
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

    private static string Rewrite(
        string text,
        string path,
        IReadOnlyDictionary<SecretUsage, string> usageToHandleToken)
    {
        return SecretPlaceholderRegex().Replace(text, match =>
        {
            var usage = new SecretUsage(match.Groups[1].Value, path);
            return usageToHandleToken.TryGetValue(usage, out var handle)
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
