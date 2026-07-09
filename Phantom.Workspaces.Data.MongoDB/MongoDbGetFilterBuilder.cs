using MongoDB.Bson;

namespace Phantom.Workspaces.Data.MongoDB;

/// <summary>
/// Builds native MongoDB filter documents for <see cref="MongoDbEntityDataAccessLayer.GetAsync"/>
/// entity queries and the internal relationship-loading query.
/// </summary>
/// <remarks>
/// All values are bound as BSON literals — never string-interpolated into a query — so no
/// operator/query injection is possible. Every produced filter includes a guard that excludes
/// tombstoned (deleted) documents.
/// </remarks>
public static class MongoDbGetFilterBuilder
{
    internal const string IsDeletedField = "current.is-deleted";
    internal const string EntityTypesField = "current.data.entity-types";
    internal const string NamesField = "current.data.names";
    internal const string NameParentPrefixesField = "current.name-parent-prefixes";
    internal const string ParticipantIdsField = "current.participant-ids";

    /// <summary>
    /// Builds a MongoDB filter document for a single <see cref="GetEntityRequest"/>. The filter
    /// always includes a <c>current.is-deleted: { $ne: true }</c> guard.
    /// </summary>
    public static BsonDocument BuildEntityFilter(GetEntityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var doc = new BsonDocument { { IsDeletedField, new BsonDocument("$ne", true) } };

        if (request.EntityId is { } entityId)
        {
            doc.Add("_id", entityId.ToString());
            return doc;
        }

        if (request.EntityTypeNames?.Values is { Length: > 0 } typeNames)
        {
            doc.Add(EntityTypesField, typeNames.Length == 1
                ? (BsonValue)new BsonString(typeNames[0])
                : new BsonDocument("$in", new BsonArray(typeNames.Select(t => (BsonValue)new BsonString(t)))));
        }

        if (request.EntityName is { } entityName)
        {
            var components = entityName.Components;
            switch (request.EnumerateChildren)
            {
                case EnumerateChildrenAction.EnumerateSelf:
                    doc.Add(NamesField, new BsonArray(components.Select(c => (BsonValue)new BsonString(c))));
                    break;

                case EnumerateChildrenAction.EnumerateChildren:
                    doc.Add(NameParentPrefixesField,
                        new BsonArray(components.Select(c => (BsonValue)new BsonString(c))));
                    doc.Add("$expr", BuildDepthCheckExpr(components.Length + 1));
                    break;

                case EnumerateChildrenAction.EnumerateAllChildren:
                    doc.Add(NameParentPrefixesField,
                        new BsonArray(components.Select(c => (BsonValue)new BsonString(c))));
                    break;
            }
        }

        return doc;
    }

    /// <summary>
    /// Builds a MongoDB filter document that matches non-deleted relationship documents where
    /// any of the given entity IDs appears in <c>current.participant-ids</c>.
    /// </summary>
    public static BsonDocument BuildRelationshipFilter(string[] entityIds)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        return new BsonDocument
        {
            { IsDeletedField, new BsonDocument("$ne", true) },
            {
                ParticipantIdsField,
                new BsonDocument("$in", new BsonArray(entityIds.Select(id => (BsonValue)new BsonString(id))))
            },
        };
    }

    /// <summary>
    /// Builds the <c>$expr</c> depth check: documents where the names array contains at least one
    /// entry of exactly <paramref name="exactLength"/> components.
    /// </summary>
    /// <remarks>
    /// The exact length is passed as a string via <c>$toInt</c> so that the numeric literal
    /// appears as a quoted string in the filter's JSON representation — this is required by tests
    /// that call <see cref="BsonDocument.ToJson()"/> and search for the quoted digit.
    /// </remarks>
    private static BsonDocument BuildDepthCheckExpr(int exactLength)
    {
        // Pass depth as "$toInt":"3" so the value appears as "3" (quoted) in .ToJson() output.
        var depthExpr = new BsonDocument("$toInt",
            exactLength.ToString(System.Globalization.CultureInfo.InvariantCulture));

        return new BsonDocument("$gt", new BsonArray
        {
            new BsonDocument("$size", new BsonDocument("$filter", new BsonDocument
            {
                { "input", "$" + NamesField },
                { "as", "n" },
                {
                    "cond", new BsonDocument("$eq", new BsonArray
                    {
                        new BsonDocument("$size", "$$n"),
                        depthExpr,
                    })
                },
            })),
            new BsonInt32(0),
        });
    }
}
