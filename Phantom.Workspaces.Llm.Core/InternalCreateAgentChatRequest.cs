using AgentSchema;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Interfaces;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Llm;

internal sealed record InternalCreateAgentChatRequest
{
    public required AgentDefinition? AgentDefinition { get; init; }

    public string? AgentSessionId { get; init; }

    public AgentServices? AgentServices { get; init; }

    public required IAgentPersistenceStore ConfiguredStore { get; init; }

    public IChatClient? ClientOverride { get; init; }

    public string? DisplayNameOverride { get; init; }

    public IReadOnlyList<IAsyncDisposable>? OwnedResources { get; init; }

    public CancellationToken CancellationToken { get; init; } = default;

    /// <summary>
    /// The scheduler used to run UI-bound work (history mutations, running-item updates, the
    /// processing loop).  When set, this takes precedence over
    /// <see cref="System.Threading.SynchronizationContext.Current"/> so that callers that
    /// construct <see cref="AgentChat"/> off the UI thread (e.g. inside <c>Task.Run</c>) can
    /// still supply the UI scheduler captured before leaving the UI thread.
    /// </summary>
    public TaskScheduler? ForegroundScheduler { get; init; }
}
