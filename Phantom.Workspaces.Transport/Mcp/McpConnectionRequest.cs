using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSchema;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Transport.Mcp;

/// <summary>
/// Builds and parses the <c>{"type":"mcp","connection":{...}}</c> request that a remote-bound MCP
/// tool sends over its executor's transport channel (issue #1438, per-component-executor-binding).
/// The <c>connection</c> payload carries the arbitrary stdio/HTTP MCP server description; the remote
/// <c>RemoteMcpHostHandler</c> opens exactly that server on the bound machine and bridges it back.
/// </summary>
/// <remarks>
/// Reuse-first / no new schema: the payload mirrors the fields the shared <c>McpTransportFactory</c>
/// already consumes (endpoint, api-key placeholder, transport mode, server name). Secret placeholders
/// (for example <c>${GITHUB_TOKEN}</c>) are forwarded verbatim so they resolve in the <b>remote
/// host's</b> context, not the caller's. <c>tool-type-name</c> / <c>tool-entity-id</c> are reserved
/// for the #1439 <c>mcp-server-entity</c> scoped-resolution touchpoint and are unused here.
/// </remarks>
public static class McpConnectionRequest
{
    public const string TypeProperty = "type";
    public const string TypeValue = "mcp";
    public const string ConnectionProperty = "connection";
    public const string EndpointProperty = "endpoint";
    public const string ApiKeyProperty = "api-key";
    public const string TransportProperty = "transport";
    public const string ServerNameProperty = "server-name";

    /// <summary>Reserved for the #1439 executor-scoped <c>mcp-server-entity</c> resolution touchpoint.</summary>
    public const string ToolTypeNameProperty = "tool-type-name";

    /// <summary>Reserved for the #1439 executor-scoped <c>mcp-server-entity</c> resolution touchpoint.</summary>
    public const string ToolEntityIdProperty = "tool-entity-id";

    /// <summary>
    /// Builds the <c>{"type":"mcp","connection":{...}}</c> request from an MCP tool's connection. The
    /// endpoint / api-key / transport mode are copied verbatim (no secret resolution here).
    /// </summary>
    public static JsonElement FromTool(McpTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var (endpoint, apiKey) = tool.Connection switch
        {
            AnonymousConnection anonymous => (anonymous.Endpoint, (string?)null),
            ApiKeyConnection apiKeyConnection => (apiKeyConnection.Endpoint, apiKeyConnection.ApiKey),
            OAuthConnection oauth => (oauth.Endpoint, (string?)null),
            _ => (null, null),
        };

        var transportMode = tool is PhantomMcpTool phantomTool ? phantomTool.Transport : McpHttpTransport.Streamable;
        var serverName = string.IsNullOrWhiteSpace(tool.ServerName) ? tool.Name : tool.ServerName;

        var connection = new JsonObject
        {
            [ServerNameProperty] = serverName,
            [EndpointProperty] = endpoint,
            [TransportProperty] = transportMode switch
            {
                McpHttpTransport.Sse => "sse",
                McpHttpTransport.Auto => "auto",
                _ => "streamable",
            },
        };

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            connection[ApiKeyProperty] = apiKey;
        }

        var request = new JsonObject
        {
            [TypeProperty] = TypeValue,
            [ConnectionProperty] = connection,
        };

        return JsonSerializer.Deserialize<JsonElement>(request.ToJsonString());
    }

    /// <summary>
    /// Parses the <c>connection</c> descriptor of an <c>mcp</c> request into an <see cref="McpTool"/>
    /// the shared <c>McpTransportFactory</c> can open on the remote host. Returns <see langword="null"/>
    /// when the request is not a recognised <c>mcp</c> connection (so the listener declines it).
    /// </summary>
    public static McpTool? ToTool(JsonElement request)
    {
        if (request.ValueKind != JsonValueKind.Object
            || !request.TryGetProperty(TypeProperty, out var type)
            || !string.Equals(type.GetString(), TypeValue, StringComparison.OrdinalIgnoreCase)
            || !request.TryGetProperty(ConnectionProperty, out var connection)
            || connection.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var endpoint = GetString(connection, EndpointProperty);
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return null;
        }

        var serverName = GetString(connection, ServerNameProperty);
        var apiKey = GetString(connection, ApiKeyProperty);
        var transport = GetString(connection, TransportProperty) switch
        {
            "sse" => McpHttpTransport.Sse,
            "auto" => McpHttpTransport.Auto,
            _ => McpHttpTransport.Streamable,
        };

        Connection resolvedConnection = string.IsNullOrWhiteSpace(apiKey)
            ? new AnonymousConnection { Endpoint = endpoint }
            : new ApiKeyConnection { Endpoint = endpoint, ApiKey = apiKey };

        return new PhantomMcpTool
        {
            ServerName = serverName ?? string.Empty,
            Connection = resolvedConnection,
            Transport = transport,
        };
    }

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
