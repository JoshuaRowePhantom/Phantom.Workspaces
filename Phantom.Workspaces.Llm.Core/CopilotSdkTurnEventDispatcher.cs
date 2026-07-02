using AgentSchema;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using System.Threading.Channels;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Handles per-turn sub-agent event dispatch for <see cref="CopilotSdkChatClient"/>.
/// Routes SDK streaming events to the correct <see cref="ISubAgentChat"/> sink (root or child),
/// buffers <see cref="ToolExecutionStartEvent"/> items so tool-call arguments can be injected as
/// the first history message of any sub-agent that is spawned by that tool call, and manages
/// the race where either event may arrive first.
/// </summary>
internal sealed class CopilotSdkTurnEventDispatcher : ISubAgentChat
{
    private readonly ChannelWriter<ChatResponseUpdate> rootWriter;
    private readonly ISubAgentChatRegistry? registry;

    // Tool starts arriving on the root stream, keyed by ToolCallId, buffered so they can be
    // injected as the first message when the corresponding SubagentStartedEvent arrives later.
    private readonly Dictionary<string, ToolExecutionStartEvent> bufferedToolStarts =
        new(StringComparer.Ordinal);

    // Child sinks created by SubagentStartedEvent before the matching ToolExecutionStartEvent
    // arrived. Keyed by ParentToolCallId; flushed when the tool start arrives.
    private readonly Dictionary<string, ISubAgentChat> pendingSubAgentSinks =
        new(StringComparer.Ordinal);

    internal CopilotSdkTurnEventDispatcher(
        ChannelWriter<ChatResponseUpdate> rootWriter,
        ISubAgentChatRegistry? registry)
    {
        this.rootWriter = rootWriter;
        this.registry = registry;
    }

    private ISubAgentChat GetTarget(string? agentId)
        => string.IsNullOrEmpty(agentId)
            ? this
            : this.registry?.TryGet(agentId) ?? this;

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
                GetTarget(delta.AgentId).Push(new ChatResponseUpdate(ChatRole.Assistant, delta.Data.DeltaContent));
                break;

            case AssistantReasoningDeltaEvent reasoningDelta when !string.IsNullOrEmpty(reasoningDelta.Data?.DeltaContent):
                this.rootWriter.TryWrite(new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents = [new TextReasoningContent(reasoningDelta.Data.DeltaContent)],
                });
                break;

            case ToolExecutionStartEvent toolStart:
                GetTarget(toolStart.AgentId).Push(new ChatResponseUpdate
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
                GetTarget(toolComplete.AgentId).Push(new ChatResponseUpdate
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

                if (!string.IsNullOrEmpty(agentId) && this.registry is { } reg)
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

                break;

            case SubagentCompletedEvent completed:
                var completedId = string.IsNullOrEmpty(completed.AgentId)
                    ? completed.Data?.ToolCallId
                    : completed.AgentId;
                if (!string.IsNullOrEmpty(completedId))
                {
                    this.registry?.TryGet(completedId)?.Complete();
                }

                break;

            case SubagentFailedEvent failed:
                var failedId = string.IsNullOrEmpty(failed.AgentId)
                    ? failed.Data?.ToolCallId
                    : failed.AgentId;
                if (!string.IsNullOrEmpty(failedId))
                {
                    this.registry?.TryGet(failedId)?.Fail(
                        new AgentSubagentFailedException(failed.Data?.Error));
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
