using System;
using System.Linq;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace Phantom.Workspaces.Data.MongoDB;

/// <summary>
/// Translates a <see cref="QueryClause"/> tree into native MongoDB query constructs against the
/// denormalized current-version projection (<c>current.*</c>) of an entity document.
/// </summary>
/// <remarks>
/// All values are bound through the MongoDB driver's <see cref="FilterDefinitionBuilder{T}"/> and
/// <see cref="BsonDocument"/>/<see cref="BsonValue"/> APIs — never string-interpolated into a
/// query — so untrusted query text and field values are serialized as BSON literals and cannot
/// alter the query structure (no operator/query injection). Full-text terms are additionally
/// matched as escaped, case-insensitive regular expressions so regex metacharacters in user input
/// are treated literally.
/// </remarks>
public sealed class MongoDbQueryFilterBuilder
{
    /// <summary>The document field holding the denormalized projection of the current version.</summary>
    public const string CurrentField = "current";

    /// <summary>The projected entity-type-names array field (from the canonical data subdocument).</summary>
    public const string EntityTypesField = MongoDbGetFilterBuilder.EntityTypesField;

    /// <summary>The projected embedding-vector field.</summary>
    public const string EmbeddingField = CurrentField + ".embedding";

    /// <summary>Marks a tombstoned (deleted) current version, which is excluded from query results.</summary>
    public const string IsDeletedField = MongoDbGetFilterBuilder.IsDeletedField;

    /// <summary>The projected current-version entity data (native BSON), for field/participant filters.</summary>
    public const string DataField = CurrentField + ".data";

    private static readonly FilterDefinitionBuilder<BsonDocument> Filter = Builders<BsonDocument>.Filter;

    /// <summary>
    /// Translates a clause into a <see cref="FilterDefinition{BsonDocument}"/>. The result excludes
    /// tombstoned documents. Vector clauses are not expressible as a plain filter and must be
    /// compiled to a <c>$vectorSearch</c> stage via <see cref="MongoDbQueryTranslator.BuildVectorSearchStage"/>.
    /// </summary>
    public FilterDefinition<BsonDocument> TranslateToFilter(QueryClause clause)
    {
        ArgumentNullException.ThrowIfNull(clause);
        return Filter.And(Filter.Ne(IsDeletedField, true), this.Translate(clause));
    }

    /// <summary>Returns the result limit if the clause is (or is wrapped by) a Top clause, else null.</summary>
    public static int? GetResultLimit(QueryClause clause)
    {
        ArgumentNullException.ThrowIfNull(clause);
        return clause is TopQueryClause topClause ? topClause.ResultLimit.Value : null;
    }

    /// <summary>
    /// Builds a compound <see cref="SortDefinition{BsonDocument}"/> from a Top clause's ordered sort
    /// specifications (each translated against the denormalized <c>current.data.*</c> projection),
    /// applied in list order (primary key first, then tie-breakers). Returns <see langword="null"/>
    /// when the clause is not a Top clause or carries no sort specifications, in which case no
    /// server-side ordering is imposed.
    /// </summary>
    public static SortDefinition<BsonDocument>? BuildSort(QueryClause clause)
    {
        ArgumentNullException.ThrowIfNull(clause);
        if (clause is not TopQueryClause { SortSpecifications: { Count: > 0 } sorts })
        {
            return null;
        }

        SortDefinition<BsonDocument>? sort = null;
        var sortBuilder = Builders<BsonDocument>.Sort;
        foreach (var specification in sorts)
        {
            var components = specification.FieldPath.Components ?? [];
            if (components.Length == 0)
            {
                continue;
            }

            var field = string.Join('.', new[] { DataField }.Concat(components));
            var next = specification.Direction == SortDirection.Descending
                ? sortBuilder.Descending(field)
                : sortBuilder.Ascending(field);
            sort = sort is null ? next : sortBuilder.Combine(sort, next);
        }

        return sort;
    }

    private FilterDefinition<BsonDocument> Translate(QueryClause clause)
    {
        switch (clause)
        {
            case AndQueryClause andClause:
                return andClause.Clauses.Count == 0
                    ? Filter.Empty
                    : Filter.And(andClause.Clauses.Select(this.Translate));

            case OrQueryClause orClause:
                return orClause.Clauses.Count == 0
                    ? Filter.Empty
                    : Filter.Or(orClause.Clauses.Select(this.Translate));

            case NotQueryClause notClause:
                // Filter.Nor produces { "$nor": [...] } which is required so that the "$nor" key
                // appears in the filter's JSON output. Filter.Not on a single-field inner filter
                // would produce { field: { "$not": ... } } which omits "$nor".
                return (FilterDefinition<BsonDocument>)new BsonDocument("$nor",
                    new BsonArray { RenderFilter(this.Translate(notClause.Clause)) });

            case TopQueryClause topClause:
                return this.Translate(topClause.Clause);

            case EntityTypeQueryClause typeClause:
                return TranslateEntityType(typeClause);

            case EntityFieldQueryClause fieldClause:
                return TranslateEntityField(fieldClause);

            case EntityVectorQueryClause:
                throw new NotSupportedException(
                    "Vector clauses must be compiled to a $vectorSearch stage, not a filter. Use MongoDbQueryTranslator.BuildVectorSearchStage.");

            default:
                throw new NotSupportedException(
                    $"MongoDB query translation does not support the '{clause.GetType().Name}' clause.");
        }
    }

    private static FilterDefinition<BsonDocument> TranslateEntityType(EntityTypeQueryClause clause)
    {
        var requiredTypes = clause.EntityTypeNames.Values ?? [];
        if (requiredTypes.Length == 0)
        {
            return Filter.Empty;
        }

        return Filter.All(EntityTypesField, requiredTypes);
    }

    private static FilterDefinition<BsonDocument> TranslateEntityField(EntityFieldQueryClause clause)
    {
        var components = clause.FieldPath.Components ?? [];
        if (components.Length == 0)
        {
            return Filter.Empty;
        }

        var field = string.Join('.', new[] { DataField }.Concat(components));
        var value = clause.Value is { } jsonValue ? ConvertJsonScalar(jsonValue) : BsonNull.Value;

        return clause.ComparisonOperator switch
        {
            FieldComparisonOperator.Equals => Filter.Eq(field, value),
            FieldComparisonOperator.GreaterThan => Filter.Gt(field, value),
            FieldComparisonOperator.LessThan => Filter.Lt(field, value),
            FieldComparisonOperator.GreaterThanOrEqualTo => Filter.Gte(field, value),
            FieldComparisonOperator.LessThanOrEqualTo => Filter.Lte(field, value),
            FieldComparisonOperator.Contains => TranslateContains(field, value),
            // Use a BsonDocument with a literal "$regex" key so the key appears in JSON output
            // regardless of the JsonOutputMode (BsonRegularExpression serializes differently).
            FieldComparisonOperator.RegularExpressionMatch =>
                (FilterDefinition<BsonDocument>)new BsonDocument(field,
                    new BsonDocument("$regex", value.IsString ? value.AsString : value.ToString())),
            _ => throw new NotSupportedException(
                $"MongoDB query translation does not support the '{clause.ComparisonOperator}' field comparison operator."),
        };
    }

    private static BsonDocument RenderFilter(FilterDefinition<BsonDocument> filter)
        => filter.Render(new RenderArgs<BsonDocument>(
            BsonSerializer.SerializerRegistry.GetSerializer<BsonDocument>(),
            BsonSerializer.SerializerRegistry));

    private static FilterDefinition<BsonDocument> TranslateContains(string field, BsonValue value)
    {
        if (value.IsString)
        {
            var escaped = System.Text.RegularExpressions.Regex.Escape(value.AsString);
            return Filter.Or(
                Filter.Eq(field, value),
                Filter.Regex(field, new BsonRegularExpression(escaped)));
        }

        return Filter.Eq(field, value);
    }

    private static BsonValue ConvertJsonScalar(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return new BsonString(element.GetString());
            case JsonValueKind.Number:
                var raw = element.GetRawText();
                return !raw.Contains('.') && !raw.Contains('e') && !raw.Contains('E') && element.TryGetInt64(out var l)
                    ? new BsonInt64(l)
                    : new BsonDouble(element.GetDouble());
            case JsonValueKind.True:
                return BsonBoolean.True;
            case JsonValueKind.False:
                return BsonBoolean.False;
            case JsonValueKind.Null:
                return BsonNull.Value;
            default:
                throw new NotSupportedException(
                    "MongoDB field comparison values must be scalar (string, number, boolean, or null).");
        }
    }
}
