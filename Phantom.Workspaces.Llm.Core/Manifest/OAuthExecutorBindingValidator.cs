using System;
using System.Text.Json;
using AgentSchema;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Llm.Core.Manifest;

/// <summary>
/// Load-time validation for the per-component-executor-binding OAuth-local rule (issue #1441,
/// Requirement 13): an MCP server that authenticates with <b>interactive OAuth</b> (authorization-code
/// with a loopback/localhost redirect + a browser) MUST run on the machine that can open the user's
/// browser and receive the loopback redirect — i.e. the <b>local</b> executor. Binding such a server to
/// a non-local executor is rejected.
/// </summary>
/// <remarks>
/// Reuse-first: this validator reasons over the same transport connection-descriptor an executor binding
/// resolves to (a <c>type</c>-discriminated <see cref="JsonElement"/>); it introduces no executor schema.
/// A server whose <see cref="McpTool.Connection"/> is an <see cref="OAuthConnection"/> in the
/// non-interactive host-pinned <c>entra-pinned</c> mode is NOT interactive and is unaffected; likewise a
/// key/PAT or anonymous connection.
/// </remarks>
public static class OAuthExecutorBindingValidator
{
    /// <summary>
    /// Whether <paramref name="connection"/> uses interactive OAuth: an <see cref="OAuthConnection"/>
    /// that is NOT the non-interactive host-pinned <c>entra-pinned</c> mode (the SDK resource-bound
    /// authorization-code + PKCE loopback flow).
    /// </summary>
    public static bool IsInteractiveOAuth(Connection? connection)
        => connection is OAuthConnection oauth
            && !string.Equals(
                oauth.AuthenticationMode,
                PhantomAgentSchema.EntraPinnedAuthenticationMode,
                StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the resolved connection-descriptor targets a non-local executor (any <c>type</c> other
    /// than <c>local</c>). A malformed/typeless descriptor is treated as local (behaviour-preserving).
    /// </summary>
    public static bool IsNonLocalExecutor(JsonElement boundExecutor)
        => boundExecutor.ValueKind == JsonValueKind.Object
            && boundExecutor.TryGetProperty(ExecutorBindings.TypePropertyName, out var type)
            && type.ValueKind == JsonValueKind.String
            && !string.Equals(
                type.GetString(),
                Phantom.Workspaces.Llm.Trust.ExecutionTargetResolver.LocalDescriptorType,
                StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns a human-readable violation message when <paramref name="tool"/> uses interactive OAuth and
    /// is bound to a non-local executor; otherwise <see langword="null"/>.
    /// </summary>
    public static string? Validate(McpTool tool, JsonElement boundExecutor)
    {
        ArgumentNullException.ThrowIfNull(tool);

        if (IsInteractiveOAuth(tool.Connection) && IsNonLocalExecutor(boundExecutor))
        {
            var serverName = string.IsNullOrWhiteSpace(tool.ServerName) ? tool.Name : tool.ServerName;
            return $"MCP server '{serverName}' uses interactive OAuth (authorization-code with a "
                + "loopback redirect) and cannot be bound to a non-local executor: interactive OAuth must "
                + "run on the local executor so the user's browser can complete the loopback sign-in. "
                + "Remove the 'executor' binding so it inherits the local session executor, or use a "
                + "non-interactive authentication mode.";
        }

        return null;
    }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when <paramref name="tool"/> uses interactive OAuth
    /// and is bound to a non-local executor (issue #1441, Requirement 13). No-ops otherwise.
    /// </summary>
    public static void EnsureValid(McpTool tool, JsonElement boundExecutor)
    {
        if (Validate(tool, boundExecutor) is { } violation)
        {
            throw new InvalidOperationException(violation);
        }
    }
}
