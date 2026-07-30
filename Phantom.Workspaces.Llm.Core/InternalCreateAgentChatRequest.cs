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

    public string? DescriptionOverride { get; init; }

    /// <summary>
    /// Caller-supplied sub-agent name/id (issue #1151). Independent of
    /// <see cref="DisplayNameOverride"/> — the display-name is the agent-type label; the
    /// name-override carries the invoker-chosen identity (e.g. <c>fix-crash1142</c>). Surfaced on
    /// <c>AgentChat.Name</c>.
    /// </summary>
    public string? NameOverride { get; init; }

    public IReadOnlyList<IAsyncDisposable>? OwnedResources { get; init; }

    public CancellationToken CancellationToken { get; init; } = default;

    /// <summary>
    /// The scheduler used to run UI-bound work (history mutations, running-item updates, the
    /// processing loop).  When set, this takes precedence over
    /// <see cref="System.Threading.SynchronizationContext.Current"/>. Construction and
    /// initialization must occur on the foreground context: when this is a
    /// <see cref="SynchronizationContextTaskScheduler"/>, the <see cref="AgentChat"/> constructor
    /// verifies the creating thread is on that context and throws otherwise (issue #909).
    /// </summary>
    public TaskScheduler? ForegroundScheduler { get; init; }

    /// <summary>
    /// The time source used to stamp chat-history timestamps and drive time-based waits. Defaults
    /// to <see cref="System.TimeProvider.System"/>; tests inject a fake provider for determinism.
    /// </summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// When set, overrides the <c>UseProvidedChatClientAsIs</c> value that would otherwise be
    /// resolved from <see cref="ClientOverride"/> and the client's
    /// <see cref="ISelfInvokingToolChatClient"/> status.  Used in tests to exercise the
    /// non-Copilot pipeline (with <c>FunctionInvokingChatClient</c>) while still supplying a
    /// deterministic <see cref="ClientOverride"/>.
    /// </summary>
    internal bool? OverrideUseProvidedChatClientAsIs { get; init; }

    /// <summary>
    /// Test-only seam that supplies an asynchronous chat-client creation path. When set (and
    /// <see cref="ClientOverride"/> is null), <see cref="AgentChat"/> awaits the returned task with
    /// <c>ConfigureAwait(false)</c> exactly like the real
    /// <c>AgentFactory.CreateChatClientAsync</c> call, so tests can force the client-creation await
    /// to genuinely suspend (mirroring the real Copilot SDK client) and have the post-await
    /// continuation resume off the captured foreground context. Used by
    /// <c>AgentChatForegroundContextTests</c> to reproduce issue #1098.
    /// </summary>
    internal Func<CancellationToken, Task<IChatClient>>? ChatClientFactoryOverride { get; init; }
}
