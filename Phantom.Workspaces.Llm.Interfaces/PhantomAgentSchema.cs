using System.Collections.Generic;
using AgentSchema;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// The single sanctioned entry point for loading AgentSchema agent definitions, manifests, and MCP
/// tools (issue #1416). Every load goes through a <see cref="TrackingLoadContext"/> minted here, which
/// upgrades each <see cref="McpTool"/> to a <see cref="PhantomMcpTool"/> carrying the Phantom
/// <c>type</c> transport field. AgentSchema drops the unknown <c>type</c> property on load, so this is
/// the only place the field is recovered.
/// </summary>
/// <remarks>
/// No production code may call the AgentSchema <c>FromJson</c>/<c>FromYaml</c> overloads directly or
/// mint its own <see cref="LoadContext"/> / <see cref="TrackingLoadContext"/>; otherwise that site
/// silently skips the rewrite and the transport reverts to <c>AutoDetect</c>. A source-scan guard test
/// enforces this centralization.
/// </remarks>
public static class PhantomAgentSchema
{
    public static AgentDefinition AgentDefinitionFromJson(string json)
        => AgentDefinition.FromJson(json, CreateContext());

    public static AgentDefinition AgentDefinitionFromYaml(string yaml)
        => AgentDefinition.FromYaml(yaml, CreateContext());

    public static AgentManifest AgentManifestFromJson(string json)
        => AgentManifest.FromJson(json, CreateContext());

    public static AgentManifest AgentManifestFromYaml(string yaml)
        => AgentManifest.FromYaml(yaml, CreateContext());

    public static McpTool McpToolFromJson(string json)
        => McpTool.FromJson(json, CreateContext());

    /// <summary>
    /// The single place that attaches the <c>McpTool</c> → <see cref="PhantomMcpTool"/> rewrite. Every
    /// context used by any load site is minted here, so the rewrite can never be omitted by a caller.
    /// </summary>
    public static TrackingLoadContext CreateContext() => new()
    {
        // Type-guarded and idempotent: polymorphic loads process the same instance twice, so the
        // second pass (already a PhantomMcpTool) is a no-op.
        PostProcess = static (result, data) =>
            result is McpTool tool and not PhantomMcpTool
                ? PhantomMcpTool.From(tool, ReadTransport(data))
                : result,
    };

    private static McpHttpTransport ReadTransport(Dictionary<string, object?> data)
        => data.TryGetValue("type", out var value) && value is string text
            ? text.Trim().ToLowerInvariant() switch
            {
                "sse" => McpHttpTransport.Sse,
                "auto" => McpHttpTransport.Auto,
                _ => McpHttpTransport.Streamable,
            }
            : McpHttpTransport.Streamable;
}
