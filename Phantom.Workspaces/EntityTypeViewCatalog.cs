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
/// Metadata for an entity type view, including which fields should be displayed and how.
/// <see cref="Fields"/> is <see langword="null"/> when the view does not specify a <c>fields</c>
/// array (so all schema fields are shown); a present-but-empty list means show no fields.
/// </summary>
public sealed record EntityTypeViewDefinition(
    string EntityTypeName,
    string? ViewName,
    IReadOnlyList<EntityFieldViewDefinition>? Fields);

/// <summary>
/// Definition of how a field should be displayed in an entity card.
/// </summary>
public sealed record EntityFieldViewDefinition(
    IReadOnlyList<string> FieldPath,
    string? DisplayFormat);

/// <summary>
/// The set of entity type views known to the workspace, loaded from entity-type-view definition entities.
/// Dynamically updates when entity-type-view entities are added, removed, or changed.
/// </summary>
public sealed class EntityTypeViewCatalog : IDisposable
{
    private const string EntityTypeViewEntityType = "entity-type-view";
    private readonly SubscribedQuery? subscribedQuery;
    private Dictionary<(string EntityTypeName, string? ViewName), EntityTypeViewDefinition> entityTypeViews;

    private EntityTypeViewCatalog(SubscribedQuery subscribedQuery)
    {
        this.subscribedQuery = subscribedQuery;
        this.entityTypeViews = new Dictionary<(string, string?), EntityTypeViewDefinition>();
        this.subscribedQuery.Results.CollectionChanged += this.OnQueryResultsChanged;
        this.RefreshEntityTypeViews();
    }

    /// <summary>Creates a static catalog for testing purposes.</summary>
    public EntityTypeViewCatalog(IEnumerable<EntityTypeViewDefinition> entityTypeViews)
    {
        this.subscribedQuery = null;
        this.entityTypeViews = new Dictionary<(string, string?), EntityTypeViewDefinition>();
        foreach (var view in entityTypeViews)
        {
            this.entityTypeViews[(view.EntityTypeName, view.ViewName)] = view;
        }
    }

    /// <summary>Raised when entity type views are added, removed, or changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Creates a dynamic entity type view catalog that observes entity-type-view definition entities.</summary>
    public static async Task<EntityTypeViewCatalog> CreateAsync(EntityBroker entityBroker, CancellationToken cancellationToken = default)
    {
        var query = await entityBroker.SubscribeQueryAsync(
            new QueryRequest
            {
                Clauses =
                [
                    new TopLevelQueryClause
                    {
                        ClauseIdentifier = new QueryClauseIdentifier("entity-type-views"),
                        Clause = new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet([EntityTypeViewEntityType]) },
                    },
                ],
            },
            cancellationToken).ConfigureAwait(false);

        return new EntityTypeViewCatalog(query);
    }

    /// <summary>
    /// Gets the entity type view for the specified entity type and view.
    /// Checks view-specific first, then falls back to global (viewName = null).
    /// </summary>
    public EntityTypeViewDefinition? GetEntityTypeView(string entityTypeName, string? viewName = null)
    {
        // Try view-specific first if viewName is provided
        if (viewName is not null && this.entityTypeViews.TryGetValue((entityTypeName, viewName), out var viewSpecific))
        {
            return viewSpecific;
        }
        
        // Fall back to global entity-type-view (no view name)
        return this.entityTypeViews.TryGetValue((entityTypeName, null), out var global) ? global : null;
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
        this.RefreshEntityTypeViews();
        this.Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshEntityTypeViews()
    {
        if (this.subscribedQuery is null)
        {
            return;
        }

        var views = new Dictionary<(string EntityTypeName, string? ViewName), EntityTypeViewDefinition>();
        foreach (var definition in this.subscribedQuery.Results)
        {
            if (TryReadEntityTypeView(definition.Snapshot, out var entityTypeView))
            {
                views[(entityTypeView.EntityTypeName, entityTypeView.ViewName)] = entityTypeView;
            }
        }

        this.entityTypeViews = views;
    }

    private static bool TryReadEntityTypeView(EntitySnapshot snapshot, out EntityTypeViewDefinition entityTypeView)
    {
        entityTypeView = null!;
        if (snapshot.Data is not { } data
            || !TryReadEntityTypeNameAndView(data, out var entityTypeName, out var viewName))
        {
            return false;
        }

        var fields = ReadFields(data);

        entityTypeView = new EntityTypeViewDefinition(entityTypeName, viewName, fields);
        return true;
    }

    private static bool TryReadEntityTypeNameAndView(JsonElement data, out string entityTypeName, out string? viewName)
    {
        entityTypeName = string.Empty;
        viewName = null;
        if (!data.TryGetProperty("names", out var names) || names.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        // Look for ["entity-type-views", "<entity-type-name>"] or ["entity-type-views", "<entity-type-name>", "<view-name>"]
        foreach (var nameComponents in names.EnumerateArray())
        {
            if (nameComponents.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var length = nameComponents.GetArrayLength();
            if (length < 2 || length > 3)
            {
                continue;
            }

            if (nameComponents[0].ValueKind != JsonValueKind.String
                || !string.Equals(nameComponents[0].GetString(), "entity-type-views", StringComparison.Ordinal))
            {
                continue;
            }

            if (length == 2 && nameComponents[1].ValueKind == JsonValueKind.String)
            {
                // Global entity-type-view: ["entity-type-views", "<entity-type-name>"]
                entityTypeName = nameComponents[1].GetString()!;
                viewName = null;
                return true;
            }
            else if (length == 3
                && nameComponents[1].ValueKind == JsonValueKind.String
                && nameComponents[2].ValueKind == JsonValueKind.String)
            {
                // View-specific entity-type-view: ["entity-type-views", "<entity-type-name>", "<view-name>"]
                entityTypeName = nameComponents[1].GetString()!;
                viewName = nameComponents[2].GetString()!;
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<EntityFieldViewDefinition>? ReadFields(JsonElement data)
    {
        if (!data.TryGetProperty("fields", out var fieldsArray) || fieldsArray.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var fields = new List<EntityFieldViewDefinition>();
        foreach (var fieldElement in fieldsArray.EnumerateArray())
        {
            if (fieldElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!fieldElement.TryGetProperty("field-path", out var fieldPathElement)
                || fieldPathElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var fieldPath = new List<string>();
            foreach (var pathComponent in fieldPathElement.EnumerateArray())
            {
                if (pathComponent.ValueKind == JsonValueKind.String && pathComponent.GetString() is { } component)
                {
                    fieldPath.Add(component);
                }
            }

            if (fieldPath.Count == 0)
            {
                continue;
            }

            var displayFormat = fieldElement.TryGetProperty("display-format", out var displayFormatElement)
                                && displayFormatElement.ValueKind == JsonValueKind.String
                ? displayFormatElement.GetString()
                : null;

            fields.Add(new EntityFieldViewDefinition(fieldPath, displayFormat));
        }

        return fields;
    }
}
