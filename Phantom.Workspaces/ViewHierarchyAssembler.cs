using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces;

/// <summary>
/// A synthesized, non-stored relationship object that represents one child→ancestor edge derived
/// from an entity's naming hierarchy. Ancestor relationship objects are produced by
/// <see cref="AncestorSynthesizer"/> and are never written to the data store.
/// </summary>
public sealed class AncestorRelationshipObject
{
    public AncestorRelationshipObject(EntityId childEntityId, string[] namePrefix)
    {
        this.ChildEntityId = childEntityId;
        this.NamePrefix = namePrefix;
    }

    /// <summary>The fixed entity-types carried by every ancestor relationship object.</summary>
    public static readonly IReadOnlyList<string> EntityTypes = ["relationship", "ancestor"];

    public EntityId ChildEntityId { get; }

    /// <summary>The first N segments of the child entity's primary name.</summary>
    public string[] NamePrefix { get; }

    /// <summary>Display label for the group header: the last segment of <see cref="NamePrefix"/>.</summary>
    public string DisplayName => this.NamePrefix.Length > 0 ? this.NamePrefix[^1] : string.Empty;
}

/// <summary>
/// Synthesizes <see cref="AncestorRelationshipObject"/>s from a set of entities by extracting the
/// leading N segments of each entity's primary name. Entities whose primary name is not longer than
/// <c>namePrefixLength</c> produce no ancestor relationship object.
/// </summary>
public static class AncestorSynthesizer
{
    public static IReadOnlyList<AncestorRelationshipObject> Synthesize(
        IEnumerable<SubscribedEntityViewModel> entities,
        int namePrefixLength)
    {
        var results = new List<AncestorRelationshipObject>();
        foreach (var entity in entities)
        {
            var primaryName = ReadPrimaryName(entity);
            if (primaryName is null || primaryName.Length <= namePrefixLength)
            {
                continue;
            }

            results.Add(new AncestorRelationshipObject(entity.EntityId, primaryName[..namePrefixLength]));
        }

        return results;
    }

    private static string[]? ReadPrimaryName(SubscribedEntityViewModel entity)
    {
        if (entity.Snapshot.Data is not { } data || data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!data.TryGetProperty("names", out var names) || names.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var first = names.EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return first.EnumerateArray()
            .Where(static e => e.ValueKind == JsonValueKind.String)
            .Select(static e => e.GetString()!)
            .ToArray();
    }
}

/// <summary>
/// A node in an assembled view hierarchy: either a real entity node or a synthesized ancestor group
/// node. The structure is rendering-agnostic — it can be flattened to an indented list or bound
/// directly to a tree control.
/// </summary>
public sealed class ViewHierarchyNode
{
    /// <summary>Creates a node for a real entity.</summary>
    public ViewHierarchyNode(SubscribedEntityViewModel entity)
    {
        this.Entity = entity;
    }

    /// <summary>Creates a synthesized ancestor group node (no real entity).</summary>
    internal ViewHierarchyNode(string[] namePrefix, string displayName)
    {
        this.NamePrefix = namePrefix;
        this.DisplayName = displayName;
    }

    /// <summary>The real entity, or <see langword="null"/> for ancestor group nodes.</summary>
    public SubscribedEntityViewModel? Entity { get; }

    /// <summary>The name prefix segments that key this ancestor group, or <see langword="null"/> for real-entity nodes.</summary>
    public string[]? NamePrefix { get; }

    /// <summary>The display label for ancestor group nodes, or <see langword="null"/> for real-entity nodes.</summary>
    public string? DisplayName { get; }

    /// <summary><see langword="true"/> when this node is a synthesized ancestor group rather than a real entity.</summary>
    public bool IsAncestorGroup => this.NamePrefix is not null;

    /// <summary>
    /// <see langword="true"/> when traversed children of this node should be visible in the flat entity list.
    /// Populated by <see cref="ViewHierarchyAssembler"/> from the entity-type-view's
    /// <c>traversed-entity-display-disposition</c>; defaults to <see langword="true"/> (expanded).
    /// </summary>
    public bool IsExpanded { get; set; } = true;

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
            var traversals = await this.GetTraversalsAsync(root, "traverse-relationships", cancellationToken).ConfigureAwait(false);
            var parentNodesById = new Dictionary<EntityId, ViewHierarchyNode>();
            var seenIds = new HashSet<EntityId> { root.EntityId };

            foreach (var traversal in traversals)
            {
                if (IsAncestorTraversal(traversal))
                {
                    await this.AddAncestorGroupNodesAsync(rootNode, traversal, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    foreach (var member in await this.QueryParticipantsAsync(traversal, root.EntityId, cancellationToken).ConfigureAwait(false))
                    {
                        if (!seenIds.Add(member.EntityId))
                        {
                            continue;
                        }

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
                }
            }

            rootNode.IsExpanded = await this.IsExpandedByDefaultAsync(root, cancellationToken).ConfigureAwait(false);
            rootNodes.Add(rootNode);
        }

        return rootNodes;
    }

    /// <summary>
    /// Reads <c>traversed-entity-display-disposition</c> from the entity-type-views for the given entity.
    /// Returns <see langword="false"/> when any entity-type-view declares <c>"collapsed"</c>;
    /// returns <see langword="true"/> otherwise (the default when the field is absent).
    /// </summary>
    private async Task<bool> IsExpandedByDefaultAsync(
        SubscribedEntityViewModel entity,
        CancellationToken cancellationToken)
    {
        foreach (var entityTypeName in ReadEntityTypes(entity))
        {
            var entityTypeView = await this.GetEntityTypeViewAsync(entityTypeName, cancellationToken)
                .ConfigureAwait(false);
            if (entityTypeView?.Snapshot.Data is not { } viewData)
            {
                continue;
            }

            if (viewData.TryGetProperty("traversed-entity-display-disposition", out var disposition)
                && disposition.ValueKind == JsonValueKind.String
                && string.Equals(disposition.GetString(), "collapsed", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Synthesizes ancestor group nodes from the given ancestor traversal configuration and appends
    /// them (and their ungrouped siblings) to the root node's children.
    /// </summary>
    private async Task AddAncestorGroupNodesAsync(
        ViewHierarchyNode rootNode,
        JsonElement traversal,
        CancellationToken cancellationToken)
    {
        var entityTypeNames = ReadStringArray(traversal, "entity-type-names");
        if (entityTypeNames.Length == 0)
        {
            return;
        }

        if (!traversal.TryGetProperty("name-prefix-length", out var prefixLengthEl)
            || prefixLengthEl.ValueKind != JsonValueKind.Number)
        {
            return;
        }

        var prefixLength = prefixLengthEl.GetInt32();
        var entities = await this.QueryEntitiesByTypeAsync(entityTypeNames, cancellationToken).ConfigureAwait(false);
        var ancestorRels = AncestorSynthesizer.Synthesize(entities, prefixLength);

        var entitiesById = entities.ToDictionary(static e => e.EntityId);
        var grouped = new Dictionary<string, (string[] Prefix, List<EntityId> ChildIds)>(StringComparer.Ordinal);

        foreach (var rel in ancestorRels)
        {
            var key = string.Join("\0", rel.NamePrefix);
            if (!grouped.TryGetValue(key, out var group))
            {
                group = (rel.NamePrefix, []);
                grouped[key] = group;
            }

            group.ChildIds.Add(rel.ChildEntityId);
        }

        var groupedChildIds = grouped.Values.SelectMany(static g => g.ChildIds).ToHashSet();

        foreach (var entity in entities)
        {
            if (!groupedChildIds.Contains(entity.EntityId))
            {
                rootNode.Children.Add(new ViewHierarchyNode(entity));
            }
        }

        foreach (var (_, (prefix, childIds)) in grouped)
        {
            var groupNode = new ViewHierarchyNode(prefix, prefix[^1]);
            rootNode.Children.Add(groupNode);
            foreach (var childId in childIds)
            {
                if (entitiesById.TryGetValue(childId, out var childEntity))
                {
                    groupNode.Children.Add(new ViewHierarchyNode(childEntity));
                }
            }
        }
    }

    private async Task<IReadOnlyList<SubscribedEntityViewModel>> QueryEntitiesByTypeAsync(
        string[] entityTypeNames,
        CancellationToken cancellationToken)
    {
        var query = new QueryRequest
        {
            Clauses =
            [
                new TopLevelQueryClause
                {
                    ClauseIdentifier = new QueryClauseIdentifier("ancestor-type"),
                    Clause = new EntityTypeQueryClause
                    {
                        EntityTypeNames = new EntityTypeNameSet(entityTypeNames),
                    },
                },
            ],
        };

        var results = await this.entityBroker.GetEntitiesAsync(query, cancellationToken).ConfigureAwait(false);
        return results.ToList();
    }

    private static bool IsAncestorTraversal(JsonElement traversal)
    {
        return traversal.TryGetProperty("relationship-type", out var rt)
            && rt.ValueKind == JsonValueKind.String
            && rt.GetString() == "ancestor";
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

        var results = await this.entityBroker.GetEntitiesAsync(query, cancellationToken).ConfigureAwait(false);
        return results.ToList();
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
