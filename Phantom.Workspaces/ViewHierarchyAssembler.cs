using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces;

/// <summary>
/// A node in an assembled view hierarchy: an entity plus its nested child nodes. The structure is
/// rendering-agnostic — it can be flattened to an indented list or bound directly to a tree control.
/// </summary>
public sealed class ViewHierarchyNode
{
    public ViewHierarchyNode(SubscribedEntityViewModel entity)
    {
        this.Entity = entity;
    }

    public SubscribedEntityViewModel Entity { get; }

    public List<ViewHierarchyNode> Children { get; } = [];
}

/// <summary>
/// Assembles a view's entity hierarchy from each root entity by traversing the relationship rules its
/// entity type declares in its <c>entity-type-view</c> definition: members reached via
/// <c>traverse-relationships</c> are nested under the root, grouped under their contextual parent
/// (the entity reached via the member type's <c>parent-hierarchy-relationships</c>, deduplicated so a
/// shared parent appears once); members with no contextual parent attach directly to the root. This
/// powers the workstreams task hierarchy. Roots whose entity types declare no traversals (the common
/// case until entity-type-views are configured) produce flat, childless nodes.
/// </summary>
public sealed class ViewHierarchyAssembler
{
    private readonly EntityBroker entityBroker;

    public ViewHierarchyAssembler(EntityBroker entityBroker)
    {
        this.entityBroker = entityBroker;
    }

    /// <summary>Assembles the nested hierarchy for the given root entities, preserving their order.</summary>
    public async Task<IReadOnlyList<ViewHierarchyNode>> AssembleAsync(
        IReadOnlyList<SubscribedEntityViewModel> roots,
        CancellationToken cancellationToken = default)
    {
        var rootNodes = new List<ViewHierarchyNode>(roots.Count);
        foreach (var root in roots)
        {
            var rootNode = new ViewHierarchyNode(root);
            var parentNodesById = new Dictionary<EntityId, ViewHierarchyNode>();

            foreach (var member in await this.GetRelatedMembersAsync(root, cancellationToken).ConfigureAwait(false))
            {
                var parent = await this.GetContextualParentAsync(member, cancellationToken).ConfigureAwait(false);
                if (parent is null || parent.EntityId == root.EntityId)
                {
                    rootNode.Children.Add(new ViewHierarchyNode(member));
                    continue;
                }

                if (!parentNodesById.TryGetValue(parent.EntityId, out var parentNode))
                {
                    parentNodesById[parent.EntityId] = parentNode = new ViewHierarchyNode(parent);
                    rootNode.Children.Add(parentNode);
                }

                parentNode.Children.Add(new ViewHierarchyNode(member));
            }

            rootNodes.Add(rootNode);
        }

        return rootNodes;
    }

    private async Task<IReadOnlyList<SubscribedEntityViewModel>> GetRelatedMembersAsync(
        SubscribedEntityViewModel root,
        CancellationToken cancellationToken)
    {
        var members = new List<SubscribedEntityViewModel>();
        var seenIds = new HashSet<EntityId> { root.EntityId };

        foreach (var traversal in await this.GetTraversalsAsync(root, "traverse-relationships", cancellationToken).ConfigureAwait(false))
        {
            foreach (var participant in await this.QueryParticipantsAsync(traversal, root.EntityId, cancellationToken).ConfigureAwait(false))
            {
                if (seenIds.Add(participant.EntityId))
                {
                    members.Add(participant);
                }
            }
        }

        return members;
    }

    private async Task<SubscribedEntityViewModel?> GetContextualParentAsync(
        SubscribedEntityViewModel member,
        CancellationToken cancellationToken)
    {
        foreach (var traversal in await this.GetTraversalsAsync(member, "parent-hierarchy-relationships", cancellationToken).ConfigureAwait(false))
        {
            foreach (var participant in await this.QueryParticipantsAsync(traversal, member.EntityId, cancellationToken).ConfigureAwait(false))
            {
                if (participant.EntityId != member.EntityId)
                {
                    return participant;
                }
            }
        }

        return null;
    }

    /// <summary>Reads the relationship-traversal rules declared by the entity's types under the given property.</summary>
    private async Task<IReadOnlyList<JsonElement>> GetTraversalsAsync(
        SubscribedEntityViewModel entity,
        string traversalProperty,
        CancellationToken cancellationToken)
    {
        var traversals = new List<JsonElement>();
        foreach (var entityTypeName in ReadEntityTypes(entity))
        {
            var entityTypeView = await this.GetEntityTypeViewAsync(entityTypeName, cancellationToken).ConfigureAwait(false);
            if (entityTypeView?.Snapshot.Data is not { } viewData
                || !viewData.TryGetProperty(traversalProperty, out var declared)
                || declared.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            traversals.AddRange(declared.EnumerateArray());
        }

        return traversals;
    }

    // TODO: This method creates a subscription but only takes a snapshot, making hierarchy static.
    // To make hierarchies dynamic: maintain all subscriptions, observe Results.CollectionChanged,
    // rebuild hierarchy when any subscription changes, dispose all on view disposal.
    private async Task<IReadOnlyCollection<SubscribedEntityViewModel>> QueryParticipantsAsync(
        JsonElement traversal,
        EntityId targetEntityId,
        CancellationToken cancellationToken)
    {
        var relationshipTypeIds = ReadStringArray(traversal, "relationship-type-ids");
        if (relationshipTypeIds.Length == 0)
        {
            return [];
        }

        var roleNames = ReadStringArray(traversal, "relationship-role-names");
        var query = new QueryRequest
        {
            Clauses =
            [
                new TopLevelQueryClause
                {
                    ClauseIdentifier = new QueryClauseIdentifier("traversal"),
                    Clause = new EntityParticipationQueryClause
                    {
                        RelationshipTypeNames = new RelationshipTypeNameSet(relationshipTypeIds),
                        ParticipationRoleNames = roleNames.Length > 0 ? new RoleNameSet(roleNames) : null,
                        MustHave = new EntityParticipationRequirement
                        {
                            Clause = new EntityFieldQueryClause
                            {
                                FieldPath = new FieldPath("entity-id"),
                                ComparisonOperator = FieldComparisonOperator.Equals,
                                Value = JsonSerializer.SerializeToElement(targetEntityId.Value.ToString()),
                            },
                        },
                    },
                },
            ],
        };

        var subscription = await this.entityBroker.SubscribeQueryAsync(query, cancellationToken).ConfigureAwait(false);
        return subscription.Results.ToList();
    }

    private async Task<SubscribedEntityViewModel?> GetEntityTypeViewAsync(
        string entityTypeName,
        CancellationToken cancellationToken)
    {
        var entities = await this.entityBroker.GetEntitiesAsync(
            [new GetEntityRequest { EntityName = new EntityName("entity-type-views", entityTypeName) }],
            cancellationToken).ConfigureAwait(false);
        return entities.FirstOrDefault();
    }

    private static IReadOnlyList<string> ReadEntityTypes(SubscribedEntityViewModel entity)
    {
        if (entity.Snapshot.Data is not { } data
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("entity-types", out var entityTypes)
            || entityTypes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return entityTypes.EnumerateArray()
            .Where(element => element.ValueKind == JsonValueKind.String)
            .Select(element => element.GetString()!)
            .ToArray();
    }

    private static string[] ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return array.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToArray();
    }
}
