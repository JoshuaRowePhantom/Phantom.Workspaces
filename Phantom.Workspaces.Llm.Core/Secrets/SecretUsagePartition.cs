using System.Collections.Generic;

namespace Phantom.Workspaces.Llm.Secrets;

/// <summary>
/// The result of partitioning an agent definition's <c>${SECRET:Name}</c> uses by dependency owner
/// (issue #1672). Required-core uses must be materialized fail-closed before the session exists;
/// deferred MCP-server uses are acquired later at the owning server's connection boundary so an
/// unavailable optional credential disables only that server instead of aborting session creation.
/// </summary>
/// <param name="RequiredCore">
/// Uses owned by the session's required core — model credentials, system prompt, and non-MCP tool
/// text. These must be materialized fail-closed before the session is created.
/// </param>
/// <param name="DeferredMcpServer">
/// Uses owned by a top-level MCP server's <c>connection</c>. Their acquisition is deferred to that
/// server's own connection boundary.
/// </param>
public sealed record SecretUsagePartition(
    IReadOnlyList<SecretUsage> RequiredCore,
    IReadOnlyList<SecretUsage> DeferredMcpServer);
