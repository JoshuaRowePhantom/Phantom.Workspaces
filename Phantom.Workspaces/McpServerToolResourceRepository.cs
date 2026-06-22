using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentSchema;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces;

/// <summary>
/// An <see cref="IToolResourceRepository"/> that exposes every <c>mcp-server</c> entity in the
/// workspace as an mcp-server-entity tool resource. The collection updates reactively as
/// mcp-server entities are added, removed, or changed.
/// </summary>
public sealed class McpServerToolResourceRepository : IToolResourceRepository, IDisposable
{
    private const string McpServerEntityType = "mcp-server";

    private readonly SubscribedQuery subscribedQuery;
    private readonly ObservableCollection<ToolResource> toolResources = [];

    private McpServerToolResourceRepository(SubscribedQuery subscribedQuery)
    {
        this.subscribedQuery = subscribedQuery;
        this.ToolResources = new ReadOnlyObservableCollection<ToolResource>(this.toolResources);
        this.subscribedQuery.Results.CollectionChanged += this.OnQueryResultsChanged;
        this.Refresh();
    }

    public ReadOnlyObservableCollection<ToolResource> ToolResources { get; }

    /// <summary>
    /// Creates a repository that observes all mcp-server entities via the supplied entity broker.
    /// </summary>
    public static async Task<McpServerToolResourceRepository> CreateAsync(
        EntityBroker entityBroker,
        CancellationToken cancellationToken = default)
    {
        var query = await entityBroker.SubscribeQueryAsync(
            new QueryRequest
            {
                Clauses =
                [
                    new TopLevelQueryClause
                    {
                        ClauseIdentifier = new QueryClauseIdentifier("entity-types"),
                        Clause = new EntityTypeQueryClause
                        {
                            EntityTypeNames = new EntityTypeNameSet([McpServerEntityType]),
                        },
                    },
                ],
            },
            cancellationToken).ConfigureAwait(false);

        return new McpServerToolResourceRepository(query);
    }

    public void Dispose()
    {
        this.subscribedQuery.Results.CollectionChanged -= this.OnQueryResultsChanged;
    }

    private void OnQueryResultsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        this.Refresh();
    }

    private void Refresh()
    {
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var resources = new List<ToolResource>();
        foreach (var entity in this.subscribedQuery.Results)
        {
            if (entity.Snapshot.Data is { } data
                && TryReadServerName(data, out var serverName)
                && seenNames.Add(serverName))
            {
                resources.Add(new ToolResource
                {
                    Kind = "tool",
                    Id = McpServerToolResourceFactory.McpServerEntityToolResourceId,
                    Name = serverName,
                });
            }
        }

        this.toolResources.Clear();
        foreach (var resource in resources)
        {
            this.toolResources.Add(resource);
        }
    }

    private static bool TryReadServerName(JsonElement data, out string serverName)
    {
        serverName = string.Empty;

        // Prefer the explicit serverName from the mcp-server configuration.
        if (data.TryGetProperty("mcp-server", out var mcpServer)
            && mcpServer.ValueKind == JsonValueKind.Object
            && mcpServer.TryGetProperty("serverName", out var serverNameElement)
            && serverNameElement.ValueKind == JsonValueKind.String
            && serverNameElement.GetString() is { Length: > 0 } explicitName)
        {
            serverName = explicitName;
            return true;
        }

        // Fall back to the last component of the entity's first name.
        if (data.TryGetProperty("names", out var names) && names.ValueKind == JsonValueKind.Array)
        {
            foreach (var nameComponents in names.EnumerateArray())
            {
                if (nameComponents.ValueKind == JsonValueKind.Array
                    && nameComponents.GetArrayLength() > 0)
                {
                    var last = nameComponents[nameComponents.GetArrayLength() - 1];
                    if (last.ValueKind == JsonValueKind.String && last.GetString() is { Length: > 0 } lastName)
                    {
                        serverName = lastName;
                        return true;
                    }
                }
            }
        }

        return false;
    }
}
