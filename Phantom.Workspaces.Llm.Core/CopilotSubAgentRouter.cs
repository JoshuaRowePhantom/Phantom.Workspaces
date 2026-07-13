using AgentSchema;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Llm.Interfaces;
using System.Text.Json;
using System.Threading.Channels;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Interprets the routed <see cref="ChatResponseUpdate"/> stream produced by
/// <see cref="CopilotSdkStreamAdapter"/> for <see cref="CopilotSdkChatClient"/> (issue #808).
/// Routes each update to the correct <see cref="ISubAgentChat"/> sink (root or child), creates
/// child <see cref="AgentChat"/> instances for new sub-agents, buffers root tool-call starts so
/// tool-call arguments can be injected as the first history message of any sub-agent spawned by
/// that tool call, and manages the race where either signal may arrive first.
/// When <see cref="IRunningAgentChatFactory"/> and <see cref="ISubAgentTable"/> are provided,
/// uses the factory path: creates an <see cref="AgentChat"/> backed by
/// <see cref="CopilotSubAgentChatClient"/> and routes updates through
/// <see cref="ICopilotSubAgentReceiver"/>. This class consumes only
/// <see cref="ChatResponseUpdate"/> items — never raw Copilot SDK event types — so its routing
/// logic is testable with pre-recorded annotated streams.
/// </summary>
internal sealed class CopilotSubAgentRouter : ISubAgentChat
{
    private static readonly AgentDefinition SubAgentDefinition =
        AgentDefinition.FromJson("""{"kind":"prompt","model":{"provider":"github-copilot-subagent"}}""")
        ?? throw new InvalidOperationException("Failed to parse sub-agent AgentDefinition.");

    private readonly ChannelWriter<ChatResponseUpdate> rootWriter;
    private readonly ISubAgentChatRegistry? registry;
    private readonly IRunningAgentChatFactory? factory;
    private readonly ISubAgentTable? subAgentTable;
    private readonly ILogger? logger;

    // Tool starts arriving on the root stream, keyed by CallId, buffered so they can be injected
    // as the first message when the corresponding sub-agent-started lifecycle signal arrives later.
    private readonly Dictionary<string, FunctionCallContent> bufferedToolStarts =
        new(StringComparer.Ordinal);

    // Child sinks created by the sub-agent-started lifecycle signal before the matching root tool
    // start arrived. Keyed by the parent tool call ID; flushed when the tool start arrives.
    private readonly Dictionary<string, ISubAgentChat> pendingSubAgentSinks =
        new(StringComparer.Ordinal);

    // Factory-path receivers, keyed by agent ID. Populated on the sub-agent-started lifecycle
    // signal when the factory path is active; subsequent updates route through these receivers.
    private readonly Dictionary<string, (RunningAgentChatLease Lease, ICopilotSubAgentReceiver Receiver)> factoryReceivers =
        new(StringComparer.Ordinal);

    internal CopilotSubAgentRouter(
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

    // ISubAgentChat — root sink
    public void Push(ChatResponseUpdate update) => this.rootWriter.TryWrite(update);
    public void Complete() { }
    public void Fail(Exception ex) { }

    /// <summary>
    /// Routes a single translated update to the correct sink, handling sub-agent lifecycle
    /// signals and buffering root tool-call starts for prompt injection.
    /// </summary>
    internal async Task RouteAsync(ChatResponseUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        if (TryGetLifecycleContent(update, out var lifecycleContent))
        {
            switch (lifecycleContent)
            {
                case FunctionCallContent { Name: CopilotSdkStreamAdapter.SubAgentStartLifecycleName } start:
                    await this.HandleSubAgentStartedAsync(start).ConfigureAwait(false);
                    break;

                case FunctionResultContent result:
                    await this.HandleSubAgentResultAsync(result).ConfigureAwait(false);
                    break;
            }

            return;
        }

        var agentId = update.Contents
            .Select(CopilotSdkStreamAdapter.GetParentToolCallId)
            .FirstOrDefault(id => !string.IsNullOrEmpty(id));

        this.PushUpdate(agentId, update);

        if (string.IsNullOrEmpty(agentId))
        {
            // Root-level tool starts are buffered so any subsequent sub-agent-started lifecycle
            // signal can inject them as the child's first history message.
            foreach (var toolStart in update.Contents.OfType<FunctionCallContent>())
            {
                this.BufferRootToolStart(toolStart);
            }
        }
    }

    private static bool TryGetLifecycleContent(ChatResponseUpdate update, out AIContent lifecycleContent)
    {
        foreach (var content in update.Contents)
        {
            if (CopilotSdkStreamAdapter.IsSubAgentLifecycleContent(content))
            {
                lifecycleContent = content;
                return true;
            }
        }

        lifecycleContent = null!;
        return false;
    }

    private void BufferRootToolStart(FunctionCallContent toolStart)
    {
        var toolCallId = toolStart.CallId;
        if (string.IsNullOrEmpty(toolCallId))
        {
            return;
        }

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

    private async Task HandleSubAgentStartedAsync(FunctionCallContent start)
    {
        var agentId = start.CallId;
        if (string.IsNullOrEmpty(agentId))
        {
            return;
        }

        var parentToolCallId = GetArgument(start, CopilotSdkStreamAdapter.ParentToolCallIdArgumentName);

        if (this.factory is not null && this.subAgentTable is not null)
        {
            await this.HandleSubAgentStartedWithFactoryAsync(agentId).ConfigureAwait(false);
        }
        else if (this.registry is { } reg)
        {
            var subDef = CreateSubAgentDefinition(
                GetArgument(start, CopilotSdkStreamAdapter.DisplayNameArgumentName),
                GetArgument(start, CopilotSdkStreamAdapter.DescriptionArgumentName));
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

    private async Task HandleSubAgentResultAsync(FunctionResultContent result)
    {
        var agentId = result.CallId;
        if (string.IsNullOrEmpty(agentId))
        {
            return;
        }

        var (failed, error) = ParseLifecycleResult(result);

        if (this.factory is not null)
        {
            if (this.factoryReceivers.TryGetValue(agentId, out var entry))
            {
                if (failed)
                {
                    entry.Receiver.Fail(new AgentSubagentFailedException(error));
                    entry.Lease.AgentChat.SetCompletionState(AgentChatCompletionState.Failed);
                }
                else
                {
                    entry.Receiver.Complete();
                    entry.Lease.AgentChat.SetCompletionState(AgentChatCompletionState.Succeeded);
                }

                this.factoryReceivers.Remove(agentId);
                await entry.Lease.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                this.logger?.LogWarning(
                    "Received {Lifecycle} for unknown sub-agent ID '{AgentId}'; ignoring.",
                    failed ? "SubagentFailed" : "SubagentCompleted",
                    agentId);
            }
        }
        else if (failed)
        {
            this.registry?.TryGet(agentId)?.Fail(new AgentSubagentFailedException(error));
        }
        else
        {
            this.registry?.TryGet(agentId)?.Complete();
        }
    }

    // Pushes an update to the correct sink: factory-path receiver → registry sink → root.
    // When the factory path is active and the agentId is non-null but unknown, logs a warning
    // and ignores the update.
    private void PushUpdate(string? agentId, ChatResponseUpdate update)
    {
        if (!string.IsNullOrEmpty(agentId) && this.factory is not null)
        {
            if (this.factoryReceivers.TryGetValue(agentId, out var entry))
            {
                entry.Receiver.Push(update);
            }
            else
            {
                this.logger?.LogWarning(
                    "Received sub-agent event for unknown sub-agent ID '{AgentId}'; ignoring.",
                    agentId);
            }

            return;
        }

        this.GetTarget(agentId).Push(update);
    }

    private ISubAgentChat GetTarget(string? agentId)
        => string.IsNullOrEmpty(agentId)
            ? this
            : this.registry?.TryGet(agentId) ?? this;

    private async Task HandleSubAgentStartedWithFactoryAsync(string agentId)
    {
        var sessionId = new AgentSessionId(Guid.NewGuid().ToString("n"));

        var lease = await this.factory!.CreateAsync(SubAgentDefinition, sessionId).ConfigureAwait(false);
        var agentChat = lease.AgentChat;

        var receiver = agentChat.GetService(typeof(ICopilotSubAgentReceiver)) as ICopilotSubAgentReceiver
            ?? throw new InvalidOperationException(
                "Sub-agent AgentChat does not expose ICopilotSubAgentReceiver. " +
                "Ensure the AgentDefinition uses the 'github-copilot-subagent' provider.");

        await this.subAgentTable!.Add(agentChat);
        this.factoryReceivers[agentId] = (lease, receiver);
    }

    private static void InjectToolCallPrompt(ISubAgentChat childSink, FunctionCallContent toolStart)
    {
        childSink.Push(new ChatResponseUpdate
        {
            Role = ChatRole.User,
            Contents = [toolStart],
        });
    }

    private static (bool Failed, string? Error) ParseLifecycleResult(FunctionResultContent result)
    {
        if (result.Result is string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var failed = document.RootElement.TryGetProperty("event", out var eventElement)
                        && eventElement.ValueKind == JsonValueKind.String
                        && eventElement.GetString() == "failed";
                    var error = document.RootElement.TryGetProperty("error", out var errorElement)
                        && errorElement.ValueKind == JsonValueKind.String
                        ? errorElement.GetString()
                        : null;
                    return (failed, error);
                }
            }
            catch (JsonException)
            {
                // Fall through: treat unparseable lifecycle payloads as completion.
            }
        }

        return (false, null);
    }

    private static string? GetArgument(FunctionCallContent call, string name)
        => call.Arguments?.TryGetValue(name, out var value) == true ? value as string : null;

    private static AgentDefinition CreateSubAgentDefinition(string? displayName, string? description)
    {
        var name = string.IsNullOrWhiteSpace(displayName) ? "sub-agent" : displayName;
        var safeName = name.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return AgentDefinition.FromJson($$"""{"kind":"prompt","name":"{{safeName}}"}""")
            ?? throw new InvalidOperationException("Failed to create sub-agent AgentDefinition.");
    }
}
