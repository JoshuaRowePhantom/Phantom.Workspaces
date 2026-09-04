using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using AgentSchema;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces;

/// <summary>
/// Resolves <c>mcp-server-entity</c> tool resources by looking up <c>mcp-server</c> entities and
/// projecting their configuration into an <see cref="McpTool"/>. Each search prefix is tried in
/// order; the resource name is appended to the prefix to form the candidate entity name, so
/// machine-specific registrations (earlier prefixes) take precedence over global registrations
/// (later prefixes).
/// </summary>
public sealed class McpServerEntityToolResourceFactory : IToolResourceFactory
{
    /// <summary>
    /// The tool resource <c>id</c> value handled by this factory.
    /// </summary>
    public const string McpServerEntityToolResourceId = "mcp-server-entity";

    private readonly IDataAccessLayer dataAccessLayer;
    private readonly IReadOnlyList<EntityName> searchPrefixes;

    /// <summary>
    /// Creates a factory that resolves mcp-server-entity tool resources against the supplied
    /// ordered search prefixes.
    /// </summary>
    /// <param name="dataAccessLayer">The data access layer used to look up mcp-server entities.</param>
    /// <param name="searchPrefixes">Ordered entity-name prefixes to search, highest priority first.</param>
    public McpServerEntityToolResourceFactory(
        IDataAccessLayer dataAccessLayer,
        IReadOnlyList<EntityName> searchPrefixes)
    {
        this.dataAccessLayer = dataAccessLayer;
        this.searchPrefixes = searchPrefixes;
    }

    public async Task<Tool?> ResolveToolResourceAsync(
        ToolResource toolResource,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(toolResource.Id, McpServerEntityToolResourceId, StringComparison.Ordinal)
            || string.IsNullOrEmpty(toolResource.Name))
        {
            return null;
        }

        foreach (var prefix in this.searchPrefixes)
        {
            var candidateName = new EntityName([.. prefix.Components, toolResource.Name]);
            var mcpServerElement = await this.TryGetMcpServerConfigAsync(candidateName, cancellationToken)
                .ConfigureAwait(false);
            if (mcpServerElement is { } configuration)
            {
                return BuildMcpTool(configuration, toolResource.Name);
            }
        }

        return null;
    }

    private async Task<JsonElement?> TryGetMcpServerConfigAsync(
        EntityName entityName,
        CancellationToken cancellationToken)
    {
        var getResult = await this.dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityName = entityName,
                    },
                ],
            },
            cancellationToken).ConfigureAwait(false);

        var entity = getResult.Batches.SelectMany(static batch => batch.Entities).FirstOrDefault();
        if (entity?.Data is not JsonElement data
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("mcp-server", out var mcpServer)
            || mcpServer.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return mcpServer;
    }

    private static McpTool BuildMcpTool(JsonElement mcpServerConfiguration, string resourceName)
    {
        var node = JsonNode.Parse(mcpServerConfiguration.GetRawText()) as JsonObject
            ?? throw new InvalidOperationException("An mcp-server configuration must be a JSON object.");

        node["kind"] = "mcp";
        node["name"] ??= node["serverName"]?.DeepClone() ?? resourceName;

        // #1416: route through the centralized Phantom load funnel so the server-level 'type' field
        // (carried in 'node') is resolved into a PhantomMcpTool.Transport rather than silently dropped
        // by AgentSchema. This is the path the bluebird Streamable-HTTP-only server uses.
        return PhantomAgentSchema.McpToolFromJson(node.ToJsonString())
            ?? throw new InvalidOperationException(
                $"Failed to construct an MCP tool from mcp-server entity '{resourceName}'.");
    }
}
