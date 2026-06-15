using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Phantom.Workspaces.Data;

/// <summary>
/// Resolves a schema document (a JSON object identified by its <c>$id</c> / absolute URL) for the
/// <see cref="SchemaDenormalizer"/>.
/// </summary>
public interface ISchemaDocumentResolver
{
    /// <summary>Resolves the schema document with the given absolute id, or null if unknown.</summary>
    JsonObject? ResolveDocument(string documentId);
}

/// <summary>
/// Produces a single, fully self-sufficient schema from a <c>$ref</c> into a set of cross-referencing
/// schema documents, by bundling every transitively referenced definition into a flat local
/// <c>$defs</c> and rewriting each <c>$ref</c> to <c>#/$defs/&lt;key&gt;</c>.
/// </summary>
/// <remarks>
/// Providers such as OpenAI cannot resolve external references, so tool input schemas that reference
/// shared documents (for example <c>workspace-entities-data-access-layer.json#/$defs/get-request</c>)
/// must be denormalized into a schema with only internal references. Recursive schemas are handled
/// because shared definitions become internal references rather than being inlined infinitely.
/// </remarks>
public sealed class SchemaDenormalizer
{
    private readonly ISchemaDocumentResolver resolver;

    /// <summary>Creates a denormalizer over the given document resolver.</summary>
    public SchemaDenormalizer(ISchemaDocumentResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        this.resolver = resolver;
    }

    /// <summary>
    /// Denormalizes the schema referenced by <paramref name="rootReference"/> into a self-sufficient
    /// schema element with no external references.
    /// </summary>
    public JsonElement Denormalize(string rootReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootReference);

        var (rootDocumentUrl, rootPointer) = SplitReference(rootReference);
        var definitions = new JsonObject();
        var keysByCanonical = new Dictionary<string, string>(StringComparer.Ordinal);
        var usedKeys = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<(string Key, string DocumentUrl, string Pointer)>();

        var root = this.ResolveAndClone(rootDocumentUrl, rootPointer);
        RewriteNode(root, rootDocumentUrl, keysByCanonical, usedKeys, pending);

        while (pending.Count > 0)
        {
            var (key, documentUrl, pointer) = pending.Dequeue();
            var definitionNode = this.ResolveAndClone(documentUrl, pointer);
            definitions[key] = definitionNode;
            RewriteNode(definitionNode, documentUrl, keysByCanonical, usedKeys, pending);
        }

        if (definitions.Count > 0)
        {
            root["$defs"] = definitions;
        }

        return JsonSerializer.SerializeToElement(root);
    }

    private JsonObject ResolveAndClone(string documentUrl, string pointer)
    {
        var node = this.ResolveNode(documentUrl, pointer)
            ?? throw new InvalidOperationException($"Could not resolve schema reference '{documentUrl}#{pointer}'.");

        if (node.DeepClone() is not JsonObject clone)
        {
            throw new InvalidOperationException($"Schema reference '{documentUrl}#{pointer}' did not resolve to an object schema.");
        }

        return clone;
    }

    private JsonNode? ResolveNode(string documentUrl, string pointer)
    {
        var document = this.resolver.ResolveDocument(documentUrl)
            ?? throw new InvalidOperationException($"Unknown schema document '{documentUrl}'.");

        JsonNode current = document;
        if (string.IsNullOrEmpty(pointer) || pointer == "/")
        {
            return current;
        }

        foreach (var rawToken in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = rawToken.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            switch (current)
            {
                case JsonObject obj when obj.TryGetPropertyValue(token, out var next) && next is not null:
                    current = next;
                    break;
                case JsonArray array when int.TryParse(token, out var index) && index >= 0 && index < array.Count && array[index] is not null:
                    current = array[index]!;
                    break;
                default:
                    return null;
            }
        }

        return current;
    }

    private static void RewriteNode(
        JsonNode? node,
        string documentUrl,
        Dictionary<string, string> keysByCanonical,
        HashSet<string> usedKeys,
        Queue<(string Key, string DocumentUrl, string Pointer)> pending)
    {
        switch (node)
        {
            case JsonObject obj:
                obj.Remove("$id");
                obj.Remove("$schema");
                obj.Remove("$comment");

                if (obj.TryGetPropertyValue("$ref", out var referenceNode)
                    && referenceNode is JsonValue referenceValue
                    && referenceValue.TryGetValue<string>(out var reference))
                {
                    var (targetDocument, targetPointer, canonical) = ResolveReferenceAbsolute(documentUrl, reference);
                    if (!keysByCanonical.TryGetValue(canonical, out var key))
                    {
                        key = AssignKey(targetDocument, targetPointer, usedKeys);
                        keysByCanonical[canonical] = key;
                        pending.Enqueue((key, targetDocument, targetPointer));
                    }

                    obj["$ref"] = $"#/$defs/{key}";
                }

                foreach (var property in obj.ToList())
                {
                    if (property.Key == "$ref")
                    {
                        continue;
                    }

                    RewriteNode(property.Value, documentUrl, keysByCanonical, usedKeys, pending);
                }

                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    RewriteNode(item, documentUrl, keysByCanonical, usedKeys, pending);
                }

                break;
        }
    }

    private static (string DocumentUrl, string Pointer, string Canonical) ResolveReferenceAbsolute(
        string documentUrl,
        string reference)
    {
        var hashIndex = reference.IndexOf('#');
        var documentPart = hashIndex >= 0 ? reference[..hashIndex] : reference;
        var pointer = hashIndex >= 0 ? reference[(hashIndex + 1)..] : string.Empty;

        var targetDocument = documentPart.Length == 0
            ? documentUrl
            : new Uri(new Uri(documentUrl), documentPart).ToString();

        return (targetDocument, pointer, $"{targetDocument}#{pointer}");
    }

    private static string AssignKey(string documentUrl, string pointer, HashSet<string> usedKeys)
    {
        var documentName = Path.GetFileNameWithoutExtension(new Uri(documentUrl).AbsolutePath);
        var pointerPart = pointer.Trim('/').Replace("$defs/", string.Empty, StringComparison.Ordinal);
        var baseKey = Sanitize(string.IsNullOrEmpty(pointerPart) ? documentName : $"{documentName}_{pointerPart}");

        var key = baseKey;
        var suffix = 2;
        while (!usedKeys.Add(key))
        {
            key = $"{baseKey}_{suffix}";
            suffix++;
        }

        return key;
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        }

        return builder.ToString();
    }

    private static (string DocumentUrl, string Pointer) SplitReference(string reference)
    {
        var hashIndex = reference.IndexOf('#');
        return hashIndex >= 0
            ? (reference[..hashIndex], reference[(hashIndex + 1)..])
            : (reference, string.Empty);
    }
}

/// <summary>
/// An <see cref="ISchemaDocumentResolver"/> backed by JSON schema documents embedded as assembly
/// resources (any resource whose name contains <c>.JsonSchemas.</c> and ends in <c>.json</c>),
/// keyed by each document's <c>$id</c>.
/// </summary>
public sealed class EmbeddedSchemaDocumentResolver : ISchemaDocumentResolver
{
    private readonly IReadOnlyDictionary<string, JsonObject> documentsById;

    /// <summary>Creates a resolver over the embedded schema documents of the given assemblies.</summary>
    public EmbeddedSchemaDocumentResolver(params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        this.documentsById = LoadDocuments(assemblies);
    }

    /// <inheritdoc />
    public JsonObject? ResolveDocument(string documentId)
        => this.documentsById.TryGetValue(documentId, out var document) ? document : null;

    private static IReadOnlyDictionary<string, JsonObject> LoadDocuments(IReadOnlyList<Assembly> assemblies)
    {
        var documents = new Dictionary<string, JsonObject>(StringComparer.Ordinal);

        foreach (var assembly in assemblies)
        {
            foreach (var resourceName in assembly.GetManifestResourceNames())
            {
                if (!resourceName.Contains(".JsonSchemas.", StringComparison.Ordinal)
                    || !resourceName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is null)
                {
                    continue;
                }

                using var reader = new StreamReader(stream);
                if (JsonNode.Parse(reader.ReadToEnd()) is JsonObject schema
                    && schema["$id"] is JsonValue idValue
                    && idValue.TryGetValue<string>(out var id))
                {
                    documents[id] = schema;
                }
            }
        }

        return documents;
    }
}
