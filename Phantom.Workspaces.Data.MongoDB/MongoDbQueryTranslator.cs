using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace Phantom.Workspaces.Data.MongoDB;

/// <summary>
/// Translates a <see cref="QueryClause"/> tree into native MongoDB query constructs against the
/// denormalized current-version projection (<c>Current.*</c>) of an entity document.
/// </summary>
/// <remarks>
/// All values are bound through the MongoDB driver's <see cref="FilterDefinitionBuilder{T}"/> and
/// <see cref="BsonDocument"/>/<see cref="BsonValue"/> APIs - never string-interpolated into a
/// query - so untrusted query text and field values are serialized as BSON literals and cannot
/// alter the query structure (no operator/query injection). Full-text terms are additionally
/// matched as escaped, case-insensitive regular expressions so regex metacharacters in user input
/// are treated literally.
/// </remarks>
public sealed class MongoDbQueryTranslator
{
    /// <summary>The document field holding the denormalized projection of the current version.</summary>
    public const string CurrentField = "current";

    /// <summary>The projected entity-type-names array field.</summary>
    public const string TypeNamesField = CurrentField + ".type-names";

    /// <summary>The projected embedding-vector field.</summary>
    public const string EmbeddingField = CurrentField + ".embedding";

    /// <summary>Marks a tombstoned (deleted) current version, which is excluded from query results.</summary>
    public const string IsDeletedField = CurrentField + ".is-deleted";

    private static readonly FilterDefinitionBuilder<BsonDocument> Filter = Builders<BsonDocument>.Filter;

    /// <summary>
    /// Translates a clause into a <see cref="FilterDefinition{BsonDocument}"/>. The result excludes
    /// tombstoned documents. Vector clauses are not expressible as a plain filter and must be
    /// compiled to a <c>$vectorSearch</c> stage via <see cref="BuildVectorSearchStage"/>.
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
                return Filter.Not(this.Translate(notClause.Clause));

            case TopQueryClause topClause:
                // The limit is applied by the caller; the filter is the inner clause's filter.
                return this.Translate(topClause.Clause);

            case EntityTypeQueryClause typeClause:
                return TranslateEntityType(typeClause);

            case EntityVectorQueryClause:
                throw new NotSupportedException(
                    "Vector clauses must be compiled to a $vectorSearch stage, not a filter. Use BuildVectorSearchStage.");

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

        // Each required type must be present in the projected type-names array. The values are bound
        // as BSON string literals by the driver, so they cannot inject query operators.
        return Filter.All(TypeNamesField, requiredTypes);
    }

    /// <summary>
    /// Builds an Atlas <c>$vectorSearch</c> aggregation stage for a vector clause. The query vector
    /// is carried as a typed BSON array (one element per dimension), and the optional pre-filter is
    /// rendered from a sibling filter, so no value is string-interpolated into the pipeline.
    /// </summary>
    /// <param name="clause">The vector clause (must carry a precomputed <see cref="EntityVectorQueryClause.QueryEmbedding"/>).</param>
    /// <param name="indexName">The Atlas vector search index name.</param>
    /// <param name="preFilter">An optional pre-filter (for example, sibling entity-type constraints).</param>
    public static BsonDocument BuildVectorSearchStage(
        EntityVectorQueryClause clause,
        string indexName,
        FilterDefinition<BsonDocument>? preFilter = null)
    {
        ArgumentNullException.ThrowIfNull(clause);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        if (clause.QueryEmbedding is not { Count: > 0 } embedding)
        {
            throw new ArgumentException(
                "BuildVectorSearchStage requires a precomputed query-embedding on the clause.",
                nameof(clause));
        }

        var limit = clause.NumberOfCandidates is { } candidates && candidates > 0 ? candidates : 10;

        var vectorSearch = new BsonDocument
        {
            { "index", indexName },
            { "path", EmbeddingField },
            { "queryVector", new BsonArray(embedding.Select(component => (BsonValue)(double)component)) },
            { "numCandidates", limit * 10 },
            { "limit", limit },
        };

        if (preFilter is not null)
        {
            var rendered = preFilter.Render(new RenderArgs<BsonDocument>(
                BsonSerializer.SerializerRegistry.GetSerializer<BsonDocument>(),
                BsonSerializer.SerializerRegistry));
            vectorSearch.Add("filter", rendered);
        }

        return new BsonDocument("$vectorSearch", vectorSearch);
    }
}
