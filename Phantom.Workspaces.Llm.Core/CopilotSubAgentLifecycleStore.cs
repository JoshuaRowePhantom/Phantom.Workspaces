namespace Phantom.Workspaces.Llm;

internal enum CopilotSubAgentLifecycleLayer { Invocation, Task, Session }
internal sealed record CopilotSubAgentCapabilities(
    string? ExecutionMode = null, bool? MultiTurn = null, string? ParentTaskId = null, string? Model = null);

internal sealed record ApplySubAgentStateRequest(
    string ChatKey,
    CopilotSubAgentLifecycleLayer Layer,
    string State,
    string? AgentId = null,
    string? TaskId = null,
    string? InvocationToolCallId = null,
    long? StatusRevision = null,
    Guid? EventId = null,
    Guid? ParentEventId = null,
    DateTimeOffset? ObservedAt = null,
    long? ExecutionBoundary = null);

/// <summary>Tracks reusable chat state separately from one-shot invocations and background tasks.</summary>
internal sealed class CopilotSubAgentLifecycleStore
{
    internal static AgentChatCompletionState ToCompletionState(string state) => state switch
    {
        "running" or "working" or "waiting" or "attention" => AgentChatCompletionState.Running,
        "failed" or "cancelled" => AgentChatCompletionState.Failed,
        "unknown" => AgentChatCompletionState.Unknown,
        _ => AgentChatCompletionState.Succeeded,
    };
    private readonly Dictionary<string, (string State, long? Revision)> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Chat, string State, string? Invocation)> tasks = new(StringComparer.Ordinal);
    // Snapshot membership is not execution history: retained task IDs can resume after eviction.
    private readonly Dictionary<string, (string Chat, string State, string? Invocation)> taskHistory = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Chat, string State, long Generation, DateTimeOffset? Timestamp)> invocations = new(StringComparer.Ordinal);
    private readonly HashSet<string> sessionSupersededByTask = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> lastNonrunningTaskStates = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> events = [];
    private readonly Dictionary<string, CopilotSubAgentCapabilities> capabilities = new(StringComparer.Ordinal);
    internal long Generation { get; private set; }
    internal long ReserveGeneration() => ++this.Generation;
    internal void Configure(string chatKey, CopilotSubAgentCapabilities value)
    {
        var old = this.capabilities.GetValueOrDefault(chatKey);
        this.capabilities[chatKey] = new(value.ExecutionMode ?? old?.ExecutionMode,
            value.MultiTurn ?? old?.MultiTurn, value.ParentTaskId ?? old?.ParentTaskId, value.Model ?? old?.Model);
    }

    internal CopilotSubAgentCapabilities? Capabilities(string chatKey) => this.capabilities.GetValueOrDefault(chatKey);
    internal string? CurrentInvocationId(string chatKey) => this.invocations.Where(i => i.Value.Chat == chatKey)
        .OrderByDescending(i => i.Value.Generation).Select(i => i.Key).FirstOrDefault();

    internal bool Apply(ApplySubAgentStateRequest request)
    {
        if (request.EventId is { } id && !this.events.Add(id)) return false;
        switch (request.Layer)
        {
            case CopilotSubAgentLifecycleLayer.Session:
                if (this.sessions.TryGetValue(request.ChatKey, out var old)
                    && old.Revision is { } revision
                    && (request.StatusRevision is not { } incoming || incoming <= revision))
                    return false;
                this.sessions[request.ChatKey] = (request.State, request.StatusRevision);
                if (request.State is "done" or "idle" && request.ExecutionBoundary is { } boundary
                    && !this.tasks.Values.Any(t => t.Chat == request.ChatKey && IsTaskActive(t.State)))
                {
                    var unresolved = this.invocations.Where(i => i.Value.Chat == request.ChatKey
                        && i.Value.State == "running" && i.Value.Generation <= boundary).ToArray();
                    foreach (var invocation in unresolved)
                        this.invocations[invocation.Key] =
                            (invocation.Value.Chat, "unknown", invocation.Value.Generation, invocation.Value.Timestamp);
                    if (unresolved.Length > 0) this.sessionSupersededByTask.Add(request.ChatKey);
                }
                break;
            case CopilotSubAgentLifecycleLayer.Task:
                if (string.IsNullOrEmpty(request.TaskId)) return false;
                if (request.ExecutionBoundary is { } taskBoundary && taskBoundary < this.Generation) return false;
                var resuming = request.State == "running"
                    && this.taskHistory.TryGetValue(request.TaskId, out var oldTask) && oldTask.State != "running";
                this.tasks[request.TaskId] = (request.ChatKey, request.State, request.InvocationToolCallId);
                this.taskHistory[request.TaskId] = this.tasks[request.TaskId];
                if (request.State == "running")
                {
                    if (resuming)
                    {
                        this.Generation++;
                        if (request.InvocationToolCallId is { Length: > 0 } resumedInvocation)
                        {
                            this.invocations.TryGetValue(resumedInvocation, out var priorInvocation);
                            if (priorInvocation.State != "running")
                                this.invocations[resumedInvocation] = (request.ChatKey, "running", this.Generation,
                                    request.ObservedAt ?? priorInvocation.Timestamp);
                        }
                    }
                    this.sessionSupersededByTask.Remove(request.ChatKey);
                    this.lastNonrunningTaskStates.Remove(request.ChatKey);
                }
                else if (request.State == "idle" || IsTerminal(request.State))
                {
                    var outcome = request.State;
                    if (outcome is "idle" or "completed"
                        && request.InvocationToolCallId is { } knownId
                        && this.invocations.TryGetValue(knownId, out var known)
                        && known.State is "failed" or "cancelled")
                        outcome = known.State;
                    var taskGeneration = this.InvocationGeneration(request.InvocationToolCallId) ?? 0;
                    if (request.InvocationToolCallId is null
                        || !this.invocations.Values.Any(i => i.Chat == request.ChatKey && i.Generation > taskGeneration))
                    {
                        this.sessionSupersededByTask.Add(request.ChatKey);
                        this.lastNonrunningTaskStates[request.ChatKey] = outcome;
                    }
                    if (request.State == "idle" && request.InvocationToolCallId is { Length: > 0 } toolCallId)
                        this.Apply(new(request.ChatKey, CopilotSubAgentLifecycleLayer.Invocation,
                            "completed", InvocationToolCallId: toolCallId));
                    if (this.sessions.TryGetValue(request.ChatKey, out var session))
                        this.sessions[request.ChatKey] = ("done", session.Revision);
                }
                break;
            case CopilotSubAgentLifecycleLayer.Invocation:
                if (string.IsNullOrEmpty(request.InvocationToolCallId)) return false;
                if (request.State == "running" && request.ObservedAt is { } startTime
                    && this.invocations.Values.Any(i => i.Chat == request.ChatKey && i.Timestamp > startTime))
                    return false;
                this.invocations.TryGetValue(request.InvocationToolCallId, out var previous);
                if (previous.Timestamp is { } timestamp
                    && request.ObservedAt is { } incomingTime && incomingTime < timestamp) return false;
                var refinesSuccess = previous.State is "completed" or "done"
                    && request.State is "failed" or "cancelled";
                if (IsTerminal(previous.State) && !refinesSuccess && (request.State != "running"
                    || request.ObservedAt is null || request.ObservedAt <= previous.Timestamp)) return false;
                var generation = request.State == "running"
                    ? request.ExecutionBoundary ?? ++this.Generation : previous.Generation;
                if (request.State != "running") this.Generation++;
                this.invocations[request.InvocationToolCallId] =
                    (request.ChatKey, request.State, generation, request.ObservedAt ?? previous.Timestamp);
                if (request.State == "running")
                {
                    this.sessionSupersededByTask.Remove(request.ChatKey);
                    this.lastNonrunningTaskStates.Remove(request.ChatKey);
                }
                else if (IsTerminal(request.State))
                {
                    if (!this.invocations.Values.Any(i => i.Chat == request.ChatKey && i.Generation > generation))
                    {
                        this.sessionSupersededByTask.Add(request.ChatKey);
                        this.lastNonrunningTaskStates[request.ChatKey] = request.State;
                    }
                    foreach (var task in this.tasks.Where(t => t.Value.Chat == request.ChatKey
                                 && t.Value.Invocation == request.InvocationToolCallId).ToArray())
                        this.tasks[task.Key] = (task.Value.Chat, request.State, task.Value.Invocation);
                    foreach (var task in this.taskHistory.Where(t => t.Value.Chat == request.ChatKey
                                 && t.Value.Invocation == request.InvocationToolCallId).ToArray())
                        this.taskHistory[task.Key] = (task.Value.Chat, request.State, task.Value.Invocation);
                }
                break;
        }
        return true;
    }

    internal void ReplaceTasks(IEnumerable<ApplySubAgentStateRequest> snapshot, long? executionBoundary = null)
    {
        if (executionBoundary is { } boundary && boundary < this.Generation) return;
        var current = snapshot.ToArray();
        var present = current.Select(t => t.TaskId).ToHashSet(StringComparer.Ordinal);
        foreach (var removed in this.tasks.Keys.Where(id => !present.Contains(id)).ToArray())
            this.tasks.Remove(removed);
        foreach (var task in current)
        {
            this.Apply(task);
            if (IsTerminal(task.State) && task.InvocationToolCallId is { Length: > 0 } toolCallId)
                this.Apply(new(task.ChatKey, CopilotSubAgentLifecycleLayer.Invocation,
                    task.State, InvocationToolCallId: toolCallId));
        }
    }

    internal bool IsInvocationTerminal(string invocationId) =>
        this.invocations.TryGetValue(invocationId, out var invocation) && IsTerminal(invocation.State);

    internal bool HasInvocation(string invocationId) => this.invocations.ContainsKey(invocationId);

    internal string? LastNonrunningTaskState(string chatKey) =>
        this.lastNonrunningTaskStates.GetValueOrDefault(chatKey);

    internal void RemoveSession(string chatKey, long? executionBoundary = null)
    {
        if (this.sessions.TryGetValue(chatKey, out var previous))
        {
            this.sessions[chatKey] = ("done", previous.Revision);
            if (executionBoundary is { } boundary
                && !this.tasks.Values.Any(t => t.Chat == chatKey && IsTaskActive(t.State)))
                foreach (var invocation in this.invocations.Where(i => i.Value.Chat == chatKey
                             && i.Value.State == "running" && i.Value.Generation <= boundary).ToArray())
                    this.invocations[invocation.Key] =
                        (invocation.Value.Chat, "unknown", invocation.Value.Generation, invocation.Value.Timestamp);
        }
    }

    internal bool CanFinishInvocation(string chatKey, string? invocationId, DateTimeOffset? timestamp)
    {
        var current = this.invocations.Where(i => i.Value.Chat == chatKey)
            .OrderByDescending(i => i.Value.Generation).FirstOrDefault();
        return current.Key is null || current.Key == invocationId
            && (current.Value.Timestamp is null || timestamp >= current.Value.Timestamp);
    }

    internal long? InvocationGeneration(string? invocationId) =>
        invocationId is not null && this.invocations.TryGetValue(invocationId, out var value) ? value.Generation : null;

    internal bool IsActive(string chatKey)
    {
        if (this.tasks.Values.Any(t => t.Chat == chatKey && IsTaskActive(t.State))) return true;
        if (this.invocations.Values.Any(i => i.Chat == chatKey && i.State == "running")) return true;
        return !this.sessionSupersededByTask.Contains(chatKey)
            && this.sessions.TryGetValue(chatKey, out var session)
            && session.State is "working" or "waiting" or "attention";
    }

    internal string Project(string chatKey)
    {
        if (this.IsActive(chatKey)) return "running";
        if (this.lastNonrunningTaskStates.TryGetValue(chatKey, out var outcome)) return outcome;
        return this.invocations.Values.Any(i => i.Chat == chatKey && i.State == "unknown")
            ? "unknown" : "completed";
    }

    private static bool IsTaskActive(string state) => state == "running";
    private static bool IsTerminal(string state) => state is "done" or "completed" or "failed" or "cancelled";
}
