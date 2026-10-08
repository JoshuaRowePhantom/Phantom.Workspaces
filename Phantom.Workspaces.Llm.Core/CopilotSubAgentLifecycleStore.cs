namespace Phantom.Workspaces.Llm;

internal enum CopilotSubAgentLifecycleLayer { Invocation, Task, Session }

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
    DateTimeOffset? ObservedAt = null);

/// <summary>Tracks reusable chat state separately from one-shot invocations and background tasks.</summary>
internal sealed class CopilotSubAgentLifecycleStore
{
    private readonly Dictionary<string, (string State, long? Revision)> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Chat, string State)> tasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Chat, string State)> invocations = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> events = [];

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
                break;
            case CopilotSubAgentLifecycleLayer.Task:
                if (string.IsNullOrEmpty(request.TaskId)) return false;
                this.tasks[request.TaskId] = (request.ChatKey, request.State);
                if (request.State == "idle")
                {
                    if (request.InvocationToolCallId is { Length: > 0 } toolCallId)
                        this.Apply(new(request.ChatKey, CopilotSubAgentLifecycleLayer.Invocation,
                            "completed", InvocationToolCallId: toolCallId));
                    if (this.sessions.TryGetValue(request.ChatKey, out var session))
                        this.sessions[request.ChatKey] = ("done", session.Revision);
                }
                break;
            case CopilotSubAgentLifecycleLayer.Invocation:
                if (string.IsNullOrEmpty(request.InvocationToolCallId)) return false;
                if (this.invocations.TryGetValue(request.InvocationToolCallId, out var previous)
                    && IsTerminal(previous.State)) return false;
                this.invocations[request.InvocationToolCallId] = (request.ChatKey, request.State);
                break;
        }
        return true;
    }

    internal void ReplaceTasks(IEnumerable<ApplySubAgentStateRequest> snapshot)
    {
        this.tasks.Clear();
        foreach (var task in snapshot)
        {
            this.Apply(task);
            if (IsTerminal(task.State) && task.InvocationToolCallId is { Length: > 0 } toolCallId)
                this.Apply(new(task.ChatKey, CopilotSubAgentLifecycleLayer.Invocation,
                    task.State, InvocationToolCallId: toolCallId));
        }
    }

    internal bool IsInvocationTerminal(string invocationId) =>
        this.invocations.TryGetValue(invocationId, out var invocation) && IsTerminal(invocation.State);

    internal void RemoveSession(string chatKey)
    {
        if (this.sessions.TryGetValue(chatKey, out var previous))
            this.sessions[chatKey] = ("done", previous.Revision);
    }

    internal bool IsActive(string chatKey)
    {
        if (this.tasks.Values.Any(t => t.Chat == chatKey && IsTaskActive(t.State))) return true;
        if (this.invocations.Values.Any(i => i.Chat == chatKey && !IsTerminal(i.State))) return true;
        return !this.tasks.Values.Any(t => t.Chat == chatKey && t.State == "idle")
            && this.sessions.TryGetValue(chatKey, out var session)
            && session.State is "working" or "waiting" or "attention";
    }

    private static bool IsTaskActive(string state) => state == "running";
    private static bool IsTerminal(string state) => state is "done" or "completed" or "failed" or "cancelled";
}
