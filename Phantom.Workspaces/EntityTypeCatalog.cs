using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces;

/// <summary>
/// Metadata for an entity type, including which interest types should be displayed on entities of this type.
/// </summary>
public sealed record EntityTypeDefinition(
    string Name,
    IReadOnlySet<string> DisplayInterestTypes);

/// <summary>
/// The set of entity types known to the workspace, loaded from entity-type definition entities.
/// Dynamically updates when entity-type entities are added, removed, or changed.
/// </summary>
public sealed class EntityTypeCatalog : IDisposable
{
    private const string EntityTypeEntityType = "entity-type";
    private readonly SubscribedQuery? subscribedQuery;
    private IReadOnlyList<EntityTypeDefinition> entityTypes;

    private EntityTypeCatalog(SubscribedQuery subscribedQuery)
    {
        this.subscribedQuery = subscribedQuery;
        this.entityTypes = [];
        this.subscribedQuery.Results.CollectionChanged += this.OnQueryResultsChanged;
        this.RefreshEntityTypes();
    }

    /// <summary>Creates a static catalog for testing purposes.</summary>
    public EntityTypeCatalog(IReadOnlyList<EntityTypeDefinition> entityTypes)
    {
        this.subscribedQuery = null;
        this.entityTypes = entityTypes;
    }

    public IReadOnlyList<EntityTypeDefinition> EntityTypes => this.entityTypes;

    /// <summary>Raised when entity types are added, removed, or changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Creates a dynamic entity type catalog that observes entity-type definition entities.</summary>
    public static async Task<EntityTypeCatalog> CreateAsync(EntityBroker entityBroker, CancellationToken cancellationToken = default)
    {
        var query = await entityBroker.SubscribeQueryAsync(
            new QueryRequest
            {
                Clauses =
                [
                    new TopLevelQueryClause
                    {
                        ClauseIdentifier = new QueryClauseIdentifier("entity-types"),
                        Clause = new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet([EntityTypeEntityType]) },
                    },
                ],
            },
            cancellationToken).ConfigureAwait(false);

        return new EntityTypeCatalog(query);
    }

    public void Dispose()
    {
        if (this.subscribedQuery is not null)
        {
            this.subscribedQuery.Results.CollectionChanged -= this.OnQueryResultsChanged;
        }
    }

    private void OnQueryResultsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        this.RefreshEntityTypes();
        this.Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshEntityTypes()
    {
        if (this.subscribedQuery is null)
        {
            return;
        }

        var types = new List<EntityTypeDefinition>();
        foreach (var definition in this.subscribedQuery.Results)
        {
            if (TryReadEntityType(definition.Snapshot, out var entityType))
            {
                types.Add(entityType);
            }
        }

        this.entityTypes = types;
    }

    private static bool TryReadEntityType(EntitySnapshot snapshot, out EntityTypeDefinition entityType)
    {
        entityType = null!;
        if (snapshot.Data is not { } data
            || !TryReadEntityTypeName(data, out var name))
        {
            return false;
        }

        var displayInterestTypes = ReadEntityTypeIds(data, "display-interest-types");

        entityType = new EntityTypeDefinition(name, displayInterestTypes);
        return true;
    }

    private static bool TryReadEntityTypeName(JsonElement data, out string name)
    {
        name = string.Empty;
        if (!data.TryGetProperty("names", out var names) || names.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        // Use the ["entity-types", "<name>"] name as the entity type's canonical name.
        foreach (var nameComponents in names.EnumerateArray())
        {
            if (nameComponents.ValueKind == JsonValueKind.Array
                && nameComponents.GetArrayLength() == 2
                && nameComponents[0].ValueKind == JsonValueKind.String
                && string.Equals(nameComponents[0].GetString(), "entity-types", System.StringComparison.Ordinal)
                && nameComponents[1].ValueKind == JsonValueKind.String)
            {
                name = nameComponents[1].GetString()!;
                return true;
            }
        }

        return false;
    }

    private static IReadOnlySet<string> ReadEntityTypeIds(JsonElement data, string propertyName)
    {
        if (!data.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return new HashSet<string>();
        }

        var entityTypeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entityTypeId in array.EnumerateArray())
        {
            if (entityTypeId.ValueKind == JsonValueKind.String && entityTypeId.GetString() is { } id)
            {
                entityTypeIds.Add(id);
            }
        }

        return entityTypeIds;
    }
}
