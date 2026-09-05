using System;
using System.Collections.Generic;
using AgentSchema;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// MCP HTTP transport negotiation mode (issue #1416). Carried by <see cref="PhantomMcpTool"/> and
/// mapped by <c>McpTransportFactory</c> onto <c>HttpClientTransportOptions.TransportMode</c>.
/// </summary>
/// <remarks>
/// <see cref="Streamable"/> is intentionally the zero value so a defaulted transport forces
/// Streamable HTTP rather than the SDK's <c>AutoDetect</c> (which issues an SSE <c>GET</c> that
/// Streamable-HTTP-only servers reject with <c>405</c>).
/// </remarks>
public enum McpHttpTransport
{
    /// <summary>Streamable HTTP (modern, default).</summary>
    Streamable,

    /// <summary>Legacy HTTP+SSE.</summary>
    Sse,

    /// <summary>SDK auto-detect (may attempt SSE first).</summary>
    Auto,
}

/// <summary>
/// Phantom-owned subclass of <see cref="McpTool"/> that carries the Phantom <c>type</c> transport
/// field (issue #1416). <see cref="McpTool"/> (Microsoft-owned AgentSchema) silently drops unknown
/// properties like <c>type</c> on load, so Phantom resolves the field itself and upgrades each
/// <see cref="McpTool"/> to a <see cref="PhantomMcpTool"/> via <see cref="PhantomAgentSchema"/>.
/// </summary>
public sealed class PhantomMcpTool : McpTool
{
    /// <summary>The negotiated HTTP transport mode. Defaults to <see cref="McpHttpTransport.Streamable"/>.</summary>
    public McpHttpTransport Transport { get; set; } = McpHttpTransport.Streamable;

    /// <summary>
    /// The name (or id) of the executor this tool is bound to (issue #1435, per-component-executor).
    /// <c>null</c> when the tool inherits the agent's default executor. AgentSchema drops the unknown
    /// <c>executor</c> property on load, so <see cref="PhantomAgentSchema"/> recovers it.
    /// </summary>
    public string? Executor { get; set; }

    /// <summary>
    /// Builds a <see cref="PhantomMcpTool"/> from a plain <see cref="McpTool"/>, copying every base
    /// field and attaching <paramref name="transport"/> and <paramref name="executor"/>.
    /// </summary>
    public static PhantomMcpTool From(McpTool source, McpHttpTransport transport, string? executor = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new PhantomMcpTool
        {
            Name = source.Name,
            Kind = source.Kind,
            Description = source.Description,
            Bindings = source.Bindings,
            Connection = source.Connection,
            ServerName = source.ServerName,
            ServerDescription = source.ServerDescription,
            ApprovalMode = source.ApprovalMode,
            AllowedTools = source.AllowedTools,
            Transport = transport,
            Executor = executor,
        };
    }

    /// <summary>
    /// Re-emits the Phantom <c>type</c> and <c>executor</c> fields so <c>ToJson()</c> →
    /// <c>FromJson()</c> round-trips preserve the transport instead of silently reverting to
    /// <c>AutoDetect</c> (issue #1416, integration point E) and preserve the executor binding
    /// (issue #1435). The <c>executor</c> field is omitted when unset to keep the JSON clean.
    /// </summary>
    public override Dictionary<string, object?> Save(SaveContext? context = null)
    {
        var result = base.Save(context!);
        result["type"] = this.Transport switch
        {
            McpHttpTransport.Sse => "sse",
            McpHttpTransport.Auto => "auto",
            _ => "streamable",
        };
        if (!string.IsNullOrWhiteSpace(this.Executor))
        {
            result["executor"] = this.Executor;
        }
        return result;
    }
}
