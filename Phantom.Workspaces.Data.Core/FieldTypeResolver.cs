using System.Text.Json;

namespace Phantom.Workspaces.Data;

public sealed class FieldTypeResolver
{
    private static readonly string[] EntitySchemaNameComponents = { "json-schemas", "https://schemas.workspaces.phantom.to/workspaces/data/core/entity.json" };
    private static readonly string EntitySchemaName = JsonSerializer.Serialize(EntitySchemaNameComponents);
    private readonly ISchemaAccessor schemaAccessor;

    public FieldTypeResolver(
        ISchemaAccessor schemaAccessor)
    {
        this.schemaAccessor = schemaAccessor;
    }

    public async Task<ResolvedFieldType> ResolveFieldTypeAsync(
        JsonElement rootEntity,
        IReadOnlyList<string> fieldPath,
        JsonElement fieldValue,
        CancellationToken cancellationToken = default)
    {
        var schemaNode = await this.ResolveFieldSchemaAsync(rootEntity, fieldPath, cancellationToken);
        if (schemaNode is null)
        {
            return new ResolvedFieldType
            {
                TypeName = InferTypeNameFromValue(fieldValue),
                EntityTypes = Array.Empty<string>(),
            };
        }

        var typeName = this.GetSchemaTypeName(schemaNode.Value, fieldValue);
        return new ResolvedFieldType
        {
            TypeName = typeName,
            DefaultMimeType = ReadDefaultMimeType(schemaNode.Value),
            EntityTypes = ReadEntityTypes(schemaNode.Value),
            SchemaNode = schemaNode,
        };
    }

    public async Task<IReadOnlyCollection<string>> EnumerateObjectFieldNamesAsync(
        JsonElement rootEntity,
        IReadOnlyList<string> objectPath,
        JsonElement objectValue,
        CancellationToken cancellationToken = default)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);
        if (objectValue.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in objectValue.EnumerateObject())
            {
                fields.Add(property.Name);
            }
        }

        var objectSchemas = await this.ResolveFieldSchemasAsync(rootEntity, objectPath, cancellationToken);
        foreach (var schemaObject in objectSchemas.Where(static schema => schema.ValueKind == JsonValueKind.Object))
        {
            this.CollectPropertyNames(schemaObject, fields);
        }

        return fields.OrderBy(static name => name, StringComparer.Ordinal).ToArray();
    }

    private async Task<JsonElement?> ResolveFieldSchemaAsync(
        JsonElement rootEntity,
        IReadOnlyList<string> fieldPath,
        CancellationToken cancellationToken)
    {
        var schemaReferences = this.GetSchemaReferencesForEntity(rootEntity);
        foreach (var schemaReference in schemaReferences)
        {
            var schemaEntity = await this.schemaAccessor.ResolveSchemaByReferenceAsync(schemaReference, cancellationToken);
            if (schemaEntity is null)
            {
                continue;
            }

            var rootSchemaNode = GetSchemaPayloadOrSelf(schemaEntity.Value);
            if (rootSchemaNode is null)
            {
                continue;
            }

            var resolved = await this.TryResolvePathInSchemaNodeAsync(
                rootSchemaNode.Value,
                fieldPath,
                0,
                currentSchemaId: GetSchemaId(schemaEntity.Value),
                visitedReferences: new HashSet<string>(StringComparer.Ordinal),
                cancellationToken);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        return null;
    }

    private async Task<IReadOnlyCollection<JsonElement>> ResolveFieldSchemasAsync(
        JsonElement rootEntity,
        IReadOnlyList<string> fieldPath,
        CancellationToken cancellationToken)
    {
        var resolvedSchemas = new List<JsonElement>();
        var schemaReferences = this.GetSchemaReferencesForEntity(rootEntity);
        foreach (var schemaReference in schemaReferences)
        {
            var schemaEntity = await this.schemaAccessor.ResolveSchemaByReferenceAsync(schemaReference, cancellationToken);
            if (schemaEntity is null)
            {
                continue;
            }

            var rootSchemaNode = GetSchemaPayloadOrSelf(schemaEntity.Value);
            if (rootSchemaNode is null)
            {
                continue;
            }

            var resolved = await this.TryResolvePathInSchemaNodeAsync(
                rootSchemaNode.Value,
                fieldPath,
                0,
                currentSchemaId: GetSchemaId(schemaEntity.Value),
                visitedReferences: new HashSet<string>(StringComparer.Ordinal),
                cancellationToken);
            if (resolved is not null)
            {
                resolvedSchemas.Add(resolved.Value);
            }
        }

        return resolvedSchemas;
    }

    private async Task<JsonElement?> TryResolvePathInSchemaNodeAsync(
        JsonElement schemaNode,
        IReadOnlyList<string> path,
        int pathIndex,
        string? currentSchemaId,
        ISet<string> visitedReferences,
        CancellationToken cancellationToken)
    {
        if (schemaNode.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (pathIndex >= path.Count)
        {
            return schemaNode;
        }

        var dereferencedNode = await this.ResolveReferenceNodeAsync(
            schemaNode,
            currentSchemaId,
            visitedReferences,
            cancellationToken);
        if (dereferencedNode is null)
        {
            return null;
        }

        schemaNode = dereferencedNode.Value;
        var segment = path[pathIndex];
        if (schemaNode.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object
            && properties.TryGetProperty(segment, out var directPropertySchema))
        {
            var directResult = await this.TryResolvePathInSchemaNodeAsync(
                directPropertySchema,
                path,
                pathIndex + 1,
                currentSchemaId,
                visitedReferences,
                cancellationToken);
            if (directResult is not null)
            {
                return directResult;
            }
        }

        if (schemaNode.TryGetProperty("additionalProperties", out var additionalProperties)
            && additionalProperties.ValueKind == JsonValueKind.Object)
        {
            var additionalResult = await this.TryResolvePathInSchemaNodeAsync(
                additionalProperties,
                path,
                pathIndex + 1,
                currentSchemaId,
                visitedReferences,
                cancellationToken);
            if (additionalResult is not null)
            {
                return additionalResult;
            }
        }

        foreach (var keyword in new[] { "allOf", "anyOf", "oneOf" })
        {
            if (!schemaNode.TryGetProperty(keyword, out var compositionSchemas)
                || compositionSchemas.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var compositionSchema in compositionSchemas.EnumerateArray())
            {
                var compositionResult = await this.TryResolvePathInSchemaNodeAsync(
                    compositionSchema,
                    path,
                    pathIndex,
                    currentSchemaId,
                    visitedReferences,
                    cancellationToken);
                if (compositionResult is not null)
                {
                    return compositionResult;
                }
            }
        }

        return null;
    }

    private async Task<JsonElement?> ResolveReferenceNodeAsync(
        JsonElement schemaNode,
        string? currentSchemaId,
        ISet<string> visitedReferences,
        CancellationToken cancellationToken)
    {
        if (!schemaNode.TryGetProperty("$ref", out var referenceElement)
            || referenceElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(referenceElement.GetString()))
        {
            return schemaNode;
        }

        var referenceValue = referenceElement.GetString()!;
        if (!visitedReferences.Add($"{currentSchemaId}|{referenceValue}"))
        {
            return null;
        }

        var (schemaReference, fragment) = ParseReference(currentSchemaId, referenceValue);
        if (schemaReference is null)
        {
            return null;
        }

        var referencedEntity = await this.schemaAccessor.ResolveSchemaByReferenceAsync(schemaReference, cancellationToken);
        if (referencedEntity is null)
        {
            return null;
        }

        var referencedSchemaNode = GetSchemaPayloadOrSelf(referencedEntity.Value);
        if (referencedSchemaNode is null)
        {
            return null;
        }

        if (string.IsNullOrEmpty(fragment))
        {
            return referencedSchemaNode;
        }

        return ResolveJsonPointer(referencedSchemaNode.Value, fragment);
    }

    private static (string? SchemaReference, string Fragment) ParseReference(
        string? currentSchemaId,
        string referenceValue)
    {
        if (referenceValue.StartsWith("#", StringComparison.Ordinal))
        {
            return (currentSchemaId, referenceValue);
        }

        if (!Uri.TryCreate(referenceValue, UriKind.Absolute, out var absoluteReferenceUri))
        {
            if (currentSchemaId is null
                || !Uri.TryCreate(currentSchemaId, UriKind.Absolute, out var currentSchemaUri)
                || !Uri.TryCreate(currentSchemaUri, referenceValue, out absoluteReferenceUri))
            {
                return (referenceValue, string.Empty);
            }
        }

        var schemaReference = $"{absoluteReferenceUri.Scheme}://{absoluteReferenceUri.Host}{absoluteReferenceUri.AbsolutePath}";
        if (!string.IsNullOrEmpty(absoluteReferenceUri.Query))
        {
            schemaReference += absoluteReferenceUri.Query;
        }

        var fragment = string.IsNullOrEmpty(absoluteReferenceUri.Fragment)
            ? string.Empty
            : absoluteReferenceUri.Fragment;
        return (schemaReference, fragment);
    }

    private static JsonElement? ResolveJsonPointer(
        JsonElement root,
        string pointer)
    {
        if (pointer == "#")
        {
            return root;
        }

        if (!pointer.StartsWith("#/", StringComparison.Ordinal))
        {
            return null;
        }

        var current = root;
        foreach (var rawSegment in pointer.Substring(2).Split('/'))
        {
            var segment = rawSegment.Replace("~1", "/").Replace("~0", "~");
            if (current.ValueKind == JsonValueKind.Object)
            {
                if (!current.TryGetProperty(segment, out current))
                {
                    return null;
                }

                continue;
            }

            if (current.ValueKind != JsonValueKind.Array
                || !int.TryParse(segment, out var index)
                || index < 0
                || index >= current.GetArrayLength())
            {
                return null;
            }

            current = current[index];
        }

        return current;
    }

    private string GetSchemaTypeName(
        JsonElement schemaNode,
        JsonElement fieldValue)
    {
        if (LooksLikeLocalStringSchema(schemaNode))
        {
            return "local-string";
        }

        if (schemaNode.TryGetProperty("$ref", out var reference)
            && reference.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(reference.GetString()))
        {
            var referenceValue = reference.GetString()!;
            if (referenceValue.Contains("core.json#/$defs/local-string", StringComparison.OrdinalIgnoreCase))
            {
                return "local-string";
            }

            if (referenceValue.Contains("mime-attachment", StringComparison.OrdinalIgnoreCase))
            {
                return "mime-attachment";
            }
        }

        if (schemaNode.TryGetProperty("type", out var typeElement))
        {
            if (typeElement.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(typeElement.GetString()))
            {
                return typeElement.GetString()!;
            }

            if (typeElement.ValueKind == JsonValueKind.Array)
            {
                var firstType = typeElement.EnumerateArray()
                    .FirstOrDefault(type => type.ValueKind == JsonValueKind.String && !string.Equals(type.GetString(), "null", StringComparison.Ordinal));
                if (firstType.ValueKind == JsonValueKind.String)
                {
                    return firstType.GetString()!;
                }
            }
        }

        foreach (var keyword in new[] { "allOf", "anyOf", "oneOf" })
        {
            if (!schemaNode.TryGetProperty(keyword, out var composedSchemas)
                || composedSchemas.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var composedSchema in composedSchemas.EnumerateArray())
            {
                var composedType = this.GetSchemaTypeName(composedSchema, fieldValue);
                if (!string.IsNullOrWhiteSpace(composedType))
                {
                    return composedType;
                }
            }
        }

        return InferTypeNameFromValue(fieldValue);
    }

    private static bool LooksLikeLocalStringSchema(
        JsonElement schemaNode)
    {
        if (!schemaNode.TryGetProperty("anyOf", out var anyOf)
            || anyOf.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var hasStringBranch = anyOf.EnumerateArray().Any(schema =>
            schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String
            && string.Equals(type.GetString(), "string", StringComparison.Ordinal));

        var hasLocalizedObjectBranch = anyOf.EnumerateArray().Any(schema =>
            schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String
            && string.Equals(type.GetString(), "object", StringComparison.Ordinal)
            && schema.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object
            && properties.TryGetProperty("default", out _));

        return hasStringBranch && hasLocalizedObjectBranch;
    }

    private static string? ReadDefaultMimeType(
        JsonElement schemaNode)
    {
        return schemaNode.TryGetProperty("x-default-mime-type", out var defaultMimeType)
               && defaultMimeType.ValueKind == JsonValueKind.String
            ? defaultMimeType.GetString()
            : null;
    }

    private static IReadOnlyCollection<string> ReadEntityTypes(
        JsonElement schemaNode)
    {
        if (!schemaNode.TryGetProperty("x-entity-type", out var xEntityType))
        {
            return Array.Empty<string>();
        }

        if (xEntityType.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(xEntityType.GetString()))
        {
            return new[] { xEntityType.GetString()! };
        }

        if (xEntityType.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return xEntityType.EnumerateArray()
            .Where(static value => value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
            .Select(static value => value.GetString()!)
            .ToArray();
    }

    private static string InferTypeNameFromValue(
        JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Object => "object",
            JsonValueKind.Array => "array",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Number => value.TryGetInt64(out _) ? "int" : "number",
            JsonValueKind.Null => "null",
            _ => "unknown",
        };
    }

    private void CollectPropertyNames(
        JsonElement schemaNode,
        ISet<string> fields)
    {
        if (schemaNode.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (schemaNode.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                fields.Add(property.Name);
            }
        }

        foreach (var keyword in new[] { "allOf", "anyOf", "oneOf" })
        {
            if (!schemaNode.TryGetProperty(keyword, out var composedSchemas)
                || composedSchemas.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var composedSchema in composedSchemas.EnumerateArray())
            {
                this.CollectPropertyNames(composedSchema, fields);
            }
        }
    }

    private IReadOnlyCollection<string> GetSchemaReferencesForEntity(
        JsonElement entityObject)
    {
        var references = new List<string>
        {
            EntitySchemaName,
        };

        if (entityObject.TryGetProperty("$schema", out var schemaElement)
            && schemaElement.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(schemaElement.GetString())
            && !string.Equals(schemaElement.GetString(), "https://json-schema.org/draft/2020-12/schema", StringComparison.Ordinal))
        {
            references.Add(schemaElement.GetString()!);
        }

        if (entityObject.TryGetProperty("entity-types", out var entityTypes)
            && entityTypes.ValueKind == JsonValueKind.Array)
        {
            foreach (var entityType in entityTypes.EnumerateArray())
            {
                if (entityType.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(entityType.GetString()))
                {
                    continue;
                }

                var entityTypeName = entityType.GetString()!;
                references.Add(JsonSerializer.Serialize(new[] { "entity-types", entityTypeName }));
                if (string.Equals(entityTypeName, "entity-type", StringComparison.Ordinal))
                {
                    references.Add(JsonSerializer.Serialize(new[] { "entity-types", "json-schema" }));
                }
            }
        }

        return references.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static JsonElement? GetSchemaPayloadOrSelf(
        JsonElement schemaEntity)
    {
        if (schemaEntity.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!schemaEntity.TryGetProperty("schema", out var schemaPayload))
        {
            return schemaEntity;
        }

        if (schemaPayload.ValueKind == JsonValueKind.Object)
        {
            return schemaPayload;
        }

        if (schemaPayload.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(schemaPayload.GetString()))
        {
            return null;
        }

        using var document = JsonDocument.Parse(
            $$"""
              {
                "$ref": "{{schemaPayload.GetString()}}"
              }
              """);
        return document.RootElement.Clone();
    }

    private static string? GetSchemaId(
        JsonElement schemaEntity)
    {
        if (SchemaAccessor.TryGetSchemaPayloadId(schemaEntity, out var schemaId))
        {
            return schemaId;
        }

        return null;
    }
}
