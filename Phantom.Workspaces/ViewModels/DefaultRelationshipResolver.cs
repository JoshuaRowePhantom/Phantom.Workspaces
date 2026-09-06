using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Shared lookup for the <c>default</c> relationship (issue #1461). A <c>default</c> relationship is
/// an entity of types <c>["entity","default","relationship"]</c> whose <c>participants.applied-to</c>
/// is the entity the default is scoped to and whose <c>participants.value</c> is the target (for
/// example the default agent manifest). Extracted from
/// <see cref="StartAgentSessionFromEntityShortcutHandler"/> so the workspace "New Agent" selection
/// tab can reuse the exact same query for default manifest pre-selection.
/// </summary>
internal static class DefaultRelationshipResolver
{
    /// <summary>
    /// Returns the <c>participants.value</c> entity id of the first <c>default</c> relationship whose
    /// <c>participants.applied-to</c> equals <paramref name="appliedToEntityId"/>, or
    /// <see langword="null"/> when none exists.
    /// </summary>
    public static async Task<EntityId?> FindDefaultValueAppliedToAsync(
        IDataAccessLayer dataAccessLayer,
        EntityId appliedToEntityId)
    {
        var queryResult = await dataAccessLayer.QueryAsync(
            new QueryRequest
            {
                Clauses =
                [
                    new TopLevelQueryClause
                    {
                        ClauseIdentifier = new QueryClauseIdentifier("default-for-entity"),
                        Clause = new AndQueryClause
                        {
                            Clauses =
                            [
                                new EntityTypeQueryClause
                                {
                                    EntityTypeNames = new EntityTypeNameSet(["default"]),
                                },
                                new EntityFieldQueryClause
                                {
                                    FieldPath = new FieldPath("participants", "applied-to"),
                                    ComparisonOperator = FieldComparisonOperator.Equals,
                                    Value = JsonSerializer.SerializeToElement(appliedToEntityId.Value.ToString()),
                                },
                            ],
                        },
                    },
                ],
            });

        foreach (var snapshot in queryResult.Batches.SelectMany(static batch => batch.Entities))
        {
            if (snapshot.Data is JsonElement data
                && data.TryGetProperty("participants", out var participants)
                && participants.TryGetProperty("value", out var valueEl))
            {
                var reference = valueEl.TryReadEntityReference();
                if (reference?.EntityId is { } entityId)
                {
                    return entityId;
                }
            }
        }

        return null;
    }
}
