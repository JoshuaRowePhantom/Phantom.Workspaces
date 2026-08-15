using System.IO;
using AgentSchema;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// A request to execute an agent definition under a resolved trust profile on a target
/// client instance.
/// </summary>
public sealed record TrustedExecutionRequest
{
    /// <summary>The agent definition to execute.</summary>
    public required AgentDefinition AgentDefinition { get; init; }

    /// <summary>The effective, composed trust profile governing execution.</summary>
    public required TrustProfile TrustProfile { get; init; }

    /// <summary>
    /// The client instance to execute on; <c>"."</c> denotes the local instance.
    /// </summary>
    public string TargetClientInstance { get; init; } = TrustProfile.LocalClientInstance;

    /// <summary>Optional existing agent session id to restore.</summary>
    public string? AgentSessionId { get; init; }

    /// <summary>Optional service integrations.</summary>
    public AgentServices? AgentServices { get; init; }

    /// <summary>
    /// When true, preserves the source's <see cref="IAgentPersistenceStore"/> instead of
    /// overriding with <see cref="NullAgentPersistenceStore"/>. Used for router-local +
    /// chat-client-remote topology where persistence lives on the source. Default false
    /// (full-remote-executor behavior).
    /// </summary>
    public bool PreserveSourcePersistence { get; init; }
}

/// <summary>
/// Executes an agent definition under a trust profile. Implementations are layered:
/// <see cref="LocalTrustedExecutor"/> (in Llm.Core) handles local execution — containers,
/// processes, and tool permissions — while remoting executors (implemented in the Workspaces
/// application layer) tunnel execution to another machine.
/// </summary>
public interface ITrustedExecutor
{
    /// <summary>Whether this executor can run on the given target client instance.</summary>
    bool CanExecute(string targetClientInstance);

    /// <summary>Creates a running <see cref="AgentChat"/> under the supplied trust profile.</summary>
    Task<AgentChat> CreateAgentChatAsync(
        TrustedExecutionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Opens a streaming session and returns its duplex byte stream.</summary>
    Task<Stream> OpenStreamAsync(TrustedStreamRequest request, CancellationToken ct = default);

    /// <summary>
    /// Executes a scheduled workspace tool on the target client instance identified by
    /// <see cref="TrustedToolRequest.TargetClientInstance"/>.
    /// </summary>
    Task RunToolAsync(TrustedToolRequest request, CancellationToken cancellationToken = default);
}
