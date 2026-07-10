using AgentSchema;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Llm.Interfaces;
using System.Threading.Channels;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Handles per-turn sub-agent event dispatch for <see cref="CopilotSdkChatClient"/>.
/// Routes SDK streaming events to the correct <see cref="ISubAgentChat"/> sink (root or child),
/// buffers <see cref="ToolExecutionStartEvent"/> items so tool-call arguments can be injected as
/// the first history message of any sub-agent that is spawned by that tool call, and manages
/// the race where either event may arrive first.
/// When <see cref="IRunningAgentChatFactory"/> and <see cref="ISubAgentTable"/> are provided,
/// uses the factory path: creates an <see cref="AgentChat"/> backed by
/// <see cref="CopilotSubAgentChatClient"/> and routes events through <see cref="ICopilotSubAgentReceiver"/>.
/// </summary>
internal sealed class CopilotSdkTurnEventDispatcher : ISubAgentChat
{
    private static readonly AgentDefinition SubAgentDefinition =
        AgentDefinition.FromJson("""{"kind":"prompt","model":{"provider":"github-copilot-subagent"}}""")
        ?? throw new InvalidOperationException("Failed to parse sub-agent AgentDefinition.");

    private readonly ChannelWriter<ChatResponseUpdate> rootWriter;
    private readonly ISubAgentChatRegistry? registry;
    private readonly IRunningAgentChatFactory? factory;
    private readonly ISubAgentTable? subAgentTable;
    private readonly ILogger? logger;

    // Tool starts arriving on the root stream, keyed by ToolCallId, buffered so they can be
    // injected as the first message when the corresponding SubagentStartedEvent arrives later.
    private readonly Dictionary<string, ToolExecutionStartEvent> bufferedToolStarts =
        new(StringComparer.Ordinal);

    // Child sinks created by SubagentStartedEvent before the matching ToolExecutionStartEvent
    // arrived. Keyed by ParentToolCallId; flushed when the tool start arrives.
    private readonly Dictionary<string, ISubAgentChat> pendingSubAgentSinks =
        new(StringComparer.Ordinal);

    // Factory-path receivers, keyed by agent ID. Populated on SubagentStartedEvent when the
    // factory path is active; subsequent events route through these receivers.
    private readonly Dictionary<string, (RunningAgentChatLease Lease, ICopilotSubAgentReceiver Receiver)> factoryReceivers =
        new(StringComparer.Ordinal);

    internal CopilotSdkTurnEventDispatcher(
        ChannelWriter<ChatResponseUpdate> rootWriter,
        ISubAgentChatRegistry? registry,
        IRunningAgentChatFactory? factory = null,
        ISubAgentTable? subAgentTable = null,
        ILogger? logger = null)
    {
        this.rootWriter = rootWriter;
        this.registry = registry;
        this.factory = factory;
        this.subAgentTable = subAgentTable;
        this.logger = logger;
    }

    /// <summary>
    /// Disposes all leases held for factory-path sub-agents that have not yet completed or failed.
    /// Called by the owning turn when the turn's subscription is torn down so no leases are leaked
    /// across turns.
    /// </summary>
    internal async Task DisposeRemainingLeasesAsync()
    {
        foreach (var (_, (lease, receiver)) in this.factoryReceivers)
        {
            receiver.Fail(new OperationCanceledException("Sub-agent disposed without completing"));
            lease.AgentChat.SetCompletionState(AgentChatCompletionState.Failed);
            await lease.DisposeAsync().ConfigureAwait(false);
        }

        this.factoryReceivers.Clear();
    }

    private ISubAgentChat GetTarget(string? agentId)
        => string.IsNullOrEmpty(agentId)
            ? this
            : this.registry?.TryGet(agentId) ?? this;

    // Returns the factory-path receiver for the given agent ID, or null if not registered.
    private ICopilotSubAgentReceiver? GetFactoryReceiver(string? agentId)
    {
        if (string.IsNullOrEmpty(agentId) || !this.factoryReceivers.TryGetValue(agentId, out var entry))
            return null;
        return entry.Receiver;
    }

    // Pushes an update to the correct sink: factory-path receiver → registry sink → root.
    // When the factory path is active and the agentId is non-null but unknown, logs a warning
    // and ignores the update.
    private void PushUpdate(string? agentId, ChatResponseUpdate update)
    {
        if (!string.IsNullOrEmpty(agentId) && this.factory is not null)
        {
            var receiver = this.GetFactoryReceiver(agentId);
            if (receiver is not null)
            {
                receiver.Push(update);
            }
            else
            {
                this.logger?.LogWarning(
                    "Received sub-agent event for unknown sub-agent ID '{AgentId}'; ignoring.",
                    agentId);
            }

            return;
        }

        GetTarget(agentId).Push(update);
    }

    // ISubAgentChat — root sink
    public void Push(ChatResponseUpdate update) => this.rootWriter.TryWrite(update);
    public void Complete() { }
    public void Fail(Exception ex) { }

    /// <summary>
    /// Dispatches a single SDK session event to the correct sink, handling sub-agent lifecycle
    /// events and buffering tool-start events for prompt injection.
    /// </summary>
    internal async Task DispatchAsync(object sessionEvent)
    {
        switch (sessionEvent)
        {
            case AssistantMessageDeltaEvent delta when !string.IsNullOrEmpty(delta.Data?.DeltaContent):
                this.PushUpdate(delta.AgentId, new ChatResponseUpdate(ChatRole.Assistant, delta.Data.DeltaContent));
                break;

            case AssistantReasoningDeltaEvent reasoningDelta when !string.IsNullOrEmpty(reasoningDelta.Data?.DeltaContent):
                this.rootWriter.TryWrite(new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents = [new TextReasoningContent(reasoningDelta.Data.DeltaContent)],
                });
                break;

            case ToolExecutionStartEvent toolStart:
                this.PushUpdate(toolStart.AgentId, new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents = [CopilotToolEventMapper.MapToolStart(toolStart)],
                });

                if (string.IsNullOrEmpty(toolStart.AgentId))
                {
                    // Root-level tool start: buffer it so any subsequent SubagentStartedEvent can
                    // inject it as the child's first history message.
                    var toolCallId = toolStart.Data?.ToolCallId;
                    if (!string.IsNullOrEmpty(toolCallId))
                    {
                        if (this.pendingSubAgentSinks.TryGetValue(toolCallId, out var pendingSink))
                        {
                            this.pendingSubAgentSinks.Remove(toolCallId);
                            InjectToolCallPrompt(pendingSink, toolStart);
                        }
                        else
                        {
                            this.bufferedToolStarts[toolCallId] = toolStart;
                        }
                    }
                }

                break;

            case ToolExecutionCompleteEvent toolComplete:
                this.PushUpdate(toolComplete.AgentId, new ChatResponseUpdate
                {
                    Role = ChatRole.Tool,
                    Contents = [CopilotToolEventMapper.MapToolComplete(toolComplete)],
                });
                break;

            case SubagentStartedEvent started:
                var agentId = string.IsNullOrEmpty(started.AgentId)
                    ? started.Data?.ToolCallId
                    : started.AgentId;
                var parentToolCallId = started.Data?.ToolCallId;

                if (!string.IsNullOrEmpty(agentId))
                {
                    if (this.factory is not null && this.subAgentTable is not null)
                    {
                        await this.HandleSubAgentStartedWithFactoryAsync(agentId).ConfigureAwait(false);
                    }
                    else if (this.registry is { } reg)
                    {
                        var subDef = CreateSubAgentDefinition(started.Data?.AgentDisplayName, started.Data?.AgentDescription);
                        var childSink = await reg.GetOrCreateAsync(agentId, subDef, parentToolCallId ?? string.Empty)
                            .ConfigureAwait(false);

                        if (!string.IsNullOrEmpty(parentToolCallId) &&
                            this.bufferedToolStarts.TryGetValue(parentToolCallId, out var bufferedStart))
                        {
                            this.bufferedToolStarts.Remove(parentToolCallId);
                            InjectToolCallPrompt(childSink, bufferedStart);
                        }
                        else if (!string.IsNullOrEmpty(parentToolCallId))
                        {
                            this.pendingSubAgentSinks[parentToolCallId] = childSink;
                        }
                    }
                }

                break;

            case SubagentCompletedEvent completed:
                var completedId = string.IsNullOrEmpty(completed.AgentId)
                    ? completed.Data?.ToolCallId
                    : completed.AgentId;
                if (!string.IsNullOrEmpty(completedId))
                {
                    if (this.factory is not null)
                    {
                        if (this.factoryReceivers.TryGetValue(completedId, out var completedEntry))
                        {
                            completedEntry.Receiver.Complete();
                            completedEntry.Lease.AgentChat.SetCompletionState(AgentChatCompletionState.Succeeded);
                            this.factoryReceivers.Remove(completedId);
                            await completedEntry.Lease.DisposeAsync().ConfigureAwait(false);
                        }
                        else
                        {
                            this.logger?.LogWarning(
                                "Received SubagentCompleted for unknown sub-agent ID '{AgentId}'; ignoring.",
                                completedId);
                        }
                    }
                    else
                    {
                        this.registry?.TryGet(completedId)?.Complete();
                    }
                }

                break;

            case SubagentFailedEvent failed:
                var failedId = string.IsNullOrEmpty(failed.AgentId)
                    ? failed.Data?.ToolCallId
                    : failed.AgentId;
                if (!string.IsNullOrEmpty(failedId))
                {
                    if (this.factory is not null)
                    {
                        if (this.factoryReceivers.TryGetValue(failedId, out var failedEntry))
                        {
                            failedEntry.Receiver.Fail(new AgentSubagentFailedException(failed.Data?.Error));
                            failedEntry.Lease.AgentChat.SetCompletionState(AgentChatCompletionState.Failed);
                            this.factoryReceivers.Remove(failedId);
                            await failedEntry.Lease.DisposeAsync().ConfigureAwait(false);
                        }
                        else
                        {
                            this.logger?.LogWarning(
                                "Received SubagentFailed for unknown sub-agent ID '{AgentId}'; ignoring.",
                                failedId);
                        }
                    }
                    else
                    {
                        this.registry?.TryGet(failedId)?.Fail(
                            new AgentSubagentFailedException(failed.Data?.Error));
                    }
                }

                break;

            case SessionErrorEvent error:
                this.rootWriter.TryComplete(new InvalidOperationException(
                    $"GitHub Copilot session error: {error.Data?.Message}"));
                break;

            case SessionIdleEvent:
                this.rootWriter.TryComplete();
                break;
        }
    }

    private async Task HandleSubAgentStartedWithFactoryAsync(string agentId)
    {
        var sessionId = new AgentSessionId(Guid.NewGuid().ToString("n"));

        var lease = await this.factory!.CreateAsync(SubAgentDefinition, sessionId).ConfigureAwait(false);
        var agentChat = lease.AgentChat;

        var receiver = agentChat.GetService(typeof(ICopilotSubAgentReceiver)) as ICopilotSubAgentReceiver
            ?? throw new InvalidOperationException(
                "Sub-agent AgentChat does not expose ICopilotSubAgentReceiver. " +
                "Ensure the AgentDefinition uses the 'github-copilot-subagent' provider.");

        this.subAgentTable!.Add(agentChat);
        this.factoryReceivers[agentId] = (lease, receiver);
    }

    private static void InjectToolCallPrompt(ISubAgentChat childSink, ToolExecutionStartEvent toolStart)
    {
        var mapped = CopilotToolEventMapper.MapToolStart(toolStart);
        childSink.Push(new ChatResponseUpdate
        {
            Role = ChatRole.User,
            Contents = [mapped],
        });
    }

    private static AgentDefinition CreateSubAgentDefinition(string? displayName, string? description)
    {
        var name = string.IsNullOrWhiteSpace(displayName) ? "sub-agent" : displayName;
        var safeName = name.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return AgentDefinition.FromJson($$"""{"kind":"prompt","name":"{{safeName}}"}""")
            ?? throw new InvalidOperationException("Failed to create sub-agent AgentDefinition.");
    }
}
