using System;
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
    /// The single place that attaches the <c>McpTool</c> → <see cref="PhantomMcpTool"/> rewrite (issue
    /// #1416) and the <c>OAuthConnection</c> → <see cref="PhantomOAuthConnection"/> rewrite (issue
    /// #1420). Every context used by any load site is minted here, so neither rewrite can be omitted by
    /// a caller.
    /// </summary>
    public static TrackingLoadContext CreateContext() => new()
    {
        // Type-guarded and idempotent: polymorphic loads process the same instance twice, so the
        // second pass (already a Phantom subclass) is a no-op.
        PostProcess = static (result, data) => result switch
        {
            // #1416: attach the dropped 'type' transport field. #1435: also recover the dropped
            // 'executor' binding field.
            McpTool tool and not PhantomMcpTool => PhantomMcpTool.From(tool, ReadTransport(data), ReadExecutor(data)),

            // #1420: for host-pinned Entra, attach the dropped 'authority' field. Only entra-pinned
            // connections are upgraded — every other OAuth connection stays a plain OAuthConnection and
            // continues to use the SDK's resource-bound provider.
            OAuthConnection oauth and not PhantomOAuthConnection
                when string.Equals(oauth.AuthenticationMode, EntraPinnedAuthenticationMode, StringComparison.OrdinalIgnoreCase)
                => PhantomOAuthConnection.From(oauth, ReadAuthority(data)),

            _ => result,
        },
    };

    /// <summary>The <c>authenticationMode</c> discriminator that selects host-pinned Entra auth (#1420).</summary>
    public const string EntraPinnedAuthenticationMode = "entra-pinned";

    private static string? ReadAuthority(Dictionary<string, object?> data)
        => data.TryGetValue("authority", out var value) && value is string text && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    private static McpHttpTransport ReadTransport(Dictionary<string, object?> data)
        => data.TryGetValue("type", out var value) && value is string text
            ? text.Trim().ToLowerInvariant() switch
            {
                "sse" => McpHttpTransport.Sse,
                "auto" => McpHttpTransport.Auto,
                _ => McpHttpTransport.Streamable,
            }
            : McpHttpTransport.Streamable;

    private static string? ReadExecutor(Dictionary<string, object?> data)
        => data.TryGetValue("executor", out var value) && value is string text && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;
}
