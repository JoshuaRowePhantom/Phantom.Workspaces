using AgentSchema;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Llm.Interfaces;
using System.Text.Json;
using System.Threading.Channels;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Interprets the routed <see cref="ChatResponseUpdate"/> stream produced by
/// <see cref="CopilotSdkStreamAdapter"/> for <see cref="CopilotSdkChatClient"/> (issues #808,
/// #1109, #1110).
///
/// Unified single-path routing (issue #1109): every sub-agent is created via
/// <see cref="IRunningAgentChatFactory"/> and registered with <see cref="ISubAgentTable"/> — the
/// old two-path split (registry vs factory) is gone, and both dependencies are required at
/// construction. Child sinks are inserted synchronously into an internal dictionary the moment
/// either a start-lifecycle signal or a routed child update is observed; updates that arrive
/// before the async <c>factory.CreateAsync</c> completes are buffered on the child sink and
/// flushed in order once the real <see cref="ICopilotSubAgentReceiver"/> is attached (issue #1110:
/// sub-agent output must never leak into the parent transcript).
///
/// This class consumes only <see cref="ChatResponseUpdate"/> items — never raw Copilot SDK event
/// types — so its routing logic is testable with pre-recorded annotated streams.
/// </summary>
internal sealed class CopilotSubAgentRouter : ISubAgentChat
{
    private static readonly AgentDefinition SubAgentDefinition =
        AgentDefinition.FromJson("""{"kind":"prompt","model":{"provider":"github-copilot-subagent"}}""")
        ?? throw new InvalidOperationException("Failed to parse sub-agent AgentDefinition.");

    private readonly ChannelWriter<ChatResponseUpdate> rootWriter;
    private readonly IRunningAgentChatFactory factory;
    private readonly ISubAgentTable subAgentTable;
    private readonly ILogger? logger;

    // Tool starts arriving on the root stream, keyed by CallId, buffered so they can be injected
    // as the first history message when the corresponding sub-agent-started lifecycle signal
    // arrives later.
    private readonly Dictionary<string, FunctionCallContent> bufferedToolStarts =
        new(StringComparer.Ordinal);

    // Sub-agent starts observed before their spawning tool call arrived, keyed by parent
    // tool-call id. When the tool call arrives the buffered start is drained here and its prompt
    // injected as the child's first message.
    private readonly Dictionary<string, ChildRoutingEntry> pendingChildrenByToolCall =
        new(StringComparer.Ordinal);

    // Fix #1109/#1110: single unified table of per-child sinks keyed by agentId. A ChildRoutingEntry
    // is created synchronously on the FIRST observation of an agentId (either a lifecycle start or
    // a routed update); it buffers updates until the async factory.CreateAsync completes and the
    // real receiver is attached, then flushes in order. This guarantees no sub-agent update is
    // ever misattributed to the parent transcript, even during the async-create race window.
    private readonly Dictionary<string, ChildRoutingEntry> childSinks =
        new(StringComparer.Ordinal);

    internal CopilotSubAgentRouter(
        ChannelWriter<ChatResponseUpdate> rootWriter,
        IRunningAgentChatFactory factory,
        ISubAgentTable subAgentTable,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(rootWriter);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(subAgentTable);

        this.rootWriter = rootWriter;
        this.factory = factory;
        this.subAgentTable = subAgentTable;
        this.logger = logger;
    }

    /// <summary>
    /// Disposes all leases held for sub-agents that have not yet completed or failed. Called by
    /// the owning turn when the turn's subscription is torn down so no leases are leaked across
    /// turns.
    /// </summary>
    internal async Task DisposeRemainingLeasesAsync()
    {
        List<ChildRoutingEntry> entries;
        lock (this.childSinks)
        {
            entries = this.childSinks.Values.ToList();
            this.childSinks.Clear();
        }

        foreach (var entry in entries)
        {
            await entry.DisposeAsync(new OperationCanceledException("Sub-agent disposed without completing")).ConfigureAwait(false);
        }
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

        ChildRoutingEntry? pendingChild;
        lock (this.pendingChildrenByToolCall)
        {
            if (this.pendingChildrenByToolCall.Remove(toolCallId, out pendingChild))
            {
                // fall through — inject below
            }
            else
            {
                this.bufferedToolStarts[toolCallId] = toolStart;
                return;
            }
        }

        InjectToolCallPrompt(pendingChild!, toolStart);
    }

    private async Task HandleSubAgentStartedAsync(FunctionCallContent start)
    {
        var agentId = start.CallId;
        if (string.IsNullOrEmpty(agentId))
        {
            return;
        }

        var parentToolCallId = GetArgument(start, CopilotSdkStreamAdapter.ParentToolCallIdArgumentName);

        // Fix #1133: read the caller-provided sub-agent name/description packed into the
        // lifecycle-start arguments by CopilotSdkStreamAdapter, so we can propagate them onto
        // the sub-agent's AgentChat below and avoid falling back to the session GUID for the
        // display name. Whitespace-only values degrade gracefully to null so AgentChat uses the
        // client-info default.
        var providedDisplayName = GetArgument(start, CopilotSdkStreamAdapter.DisplayNameArgumentName);
        var providedDescription = GetArgument(start, CopilotSdkStreamAdapter.DescriptionArgumentName);
        var displayNameOverride = string.IsNullOrWhiteSpace(providedDisplayName) ? null : providedDisplayName;
        var descriptionOverride = string.IsNullOrWhiteSpace(providedDescription) ? null : providedDescription;

        // Fix #1109: synchronously insert (or reuse) the child sink BEFORE any await, so any
        // routed update that races us cannot fall through to the parent transcript.
        ChildRoutingEntry entry;
        lock (this.childSinks)
        {
            if (!this.childSinks.TryGetValue(agentId, out entry!))
            {
                entry = new ChildRoutingEntry(agentId, this.logger);
                this.childSinks[agentId] = entry;
            }
        }

        // Handle parent-tool-call prompt injection: if the tool start already arrived, inject
        // now; otherwise register the pending child so BufferRootToolStart can inject when it
        // does arrive.
        if (!string.IsNullOrEmpty(parentToolCallId))
        {
            FunctionCallContent? buffered;
            lock (this.pendingChildrenByToolCall)
            {
                if (this.bufferedToolStarts.Remove(parentToolCallId, out buffered))
                {
                    // fall through — inject below
                }
                else
                {
                    this.pendingChildrenByToolCall[parentToolCallId] = entry;
                }
            }

            if (buffered is not null)
            {
                InjectToolCallPrompt(entry, buffered);
            }
        }

        try
        {
            var sessionId = new AgentSessionId(Guid.NewGuid().ToString("n"));
            var lease = await this.factory.CreateAsync(
                    SubAgentDefinition,
                    sessionId,
                    services: null,
                    displayNameOverride: displayNameOverride,
                    descriptionOverride: descriptionOverride)
                .ConfigureAwait(false);
            var agentChat = lease.AgentChat;

            var receiver = agentChat.GetService(typeof(ICopilotSubAgentReceiver)) as ICopilotSubAgentReceiver
                ?? throw new InvalidOperationException(
                    "Sub-agent AgentChat does not expose ICopilotSubAgentReceiver. " +
                    "Ensure the AgentDefinition uses the 'github-copilot-subagent' provider.");

            await this.subAgentTable.Add(agentChat);

            entry.Attach(lease, receiver);
        }
        catch (InvalidOperationException)
        {
            // Rethrow: this is a wiring bug (see receiver-null throw above) and callers expect it.
            throw;
        }
        catch (Exception ex)
        {
            entry.Fail(ex);
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

        ChildRoutingEntry? entry;
        lock (this.childSinks)
        {
            this.childSinks.TryGetValue(agentId, out entry);
        }

        if (entry is null)
        {
            this.logger?.LogWarning(
                "Received {Lifecycle} for unknown sub-agent ID '{AgentId}'; ignoring.",
                failed ? "SubagentFailed" : "SubagentCompleted",
                agentId);
            return;
        }

        if (failed)
        {
            await entry.CompleteAsFailedAsync(new AgentSubagentFailedException(error)).ConfigureAwait(false);
        }
        else
        {
            await entry.CompleteAsync().ConfigureAwait(false);
        }

        lock (this.childSinks)
        {
            this.childSinks.Remove(agentId);
        }
    }

    // Fix #1109/#1110: push an update to the correct sink. For a non-empty agentId we always route
    // to a per-child ChildRoutingEntry (creating a buffering entry on first sight if the start
    // lifecycle has not yet arrived). Sub-agent output is NEVER pushed to the parent transcript.
    private void PushUpdate(string? agentId, ChatResponseUpdate update)
    {
        if (string.IsNullOrEmpty(agentId))
        {
            this.rootWriter.TryWrite(update);
            return;
        }

        ChildRoutingEntry entry;
        lock (this.childSinks)
        {
            if (!this.childSinks.TryGetValue(agentId, out entry!))
            {
                entry = new ChildRoutingEntry(agentId, this.logger);
                this.childSinks[agentId] = entry;
            }
        }

        entry.Push(update);
    }

    private static void InjectToolCallPrompt(ChildRoutingEntry entry, FunctionCallContent toolStart)
    {
        entry.Push(new ChatResponseUpdate
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

    /// <summary>
    /// Per-child routing entry. Owns the (optional) buffer of updates that arrived before the
    /// async factory-create completed, the real <see cref="ICopilotSubAgentReceiver"/> once
    /// attached, and the <see cref="RunningAgentChatLease"/> that must be disposed on completion
    /// or dispose. Guarantees ordered flush and end-of-lifecycle propagation even if
    /// <see cref="Attach"/> races with <see cref="CompleteAsync"/> / <see cref="CompleteAsFailedAsync"/>.
    /// </summary>
    private sealed class ChildRoutingEntry
    {
        private readonly object gate = new();
        private readonly string agentId;
        private readonly ILogger? logger;
        private readonly List<ChatResponseUpdate> pending = new();
        private ICopilotSubAgentReceiver? receiver;
        private RunningAgentChatLease? lease;
        private bool completedSucceeded;
        private Exception? completedFailure;
        private bool endSignalled;
        private bool disposed;

        internal ChildRoutingEntry(string agentId, ILogger? logger)
        {
            this.agentId = agentId;
            this.logger = logger;
        }

        internal void Push(ChatResponseUpdate update)
        {
            ICopilotSubAgentReceiver? target;
            lock (this.gate)
            {
                if (this.disposed)
                {
                    return;
                }

                if (this.receiver is null)
                {
                    this.pending.Add(update);
                    return;
                }

                target = this.receiver;
            }

            target.Push(update);
        }

        internal void Attach(RunningAgentChatLease lease, ICopilotSubAgentReceiver receiver)
        {
            List<ChatResponseUpdate> flushed;
            bool completedSucceeded;
            Exception? failure;
            lock (this.gate)
            {
                if (this.disposed)
                {
                    // Router was torn down while we were creating; dispose the lease we just built.
                    _ = lease.DisposeAsync();
                    return;
                }

                this.receiver = receiver;
                this.lease = lease;
                flushed = new List<ChatResponseUpdate>(this.pending);
                this.pending.Clear();
                completedSucceeded = this.completedSucceeded;
                failure = this.completedFailure;
                if (this.endSignalled)
                {
                    // We're about to propagate the end signal now; make sure we don't double it.
                    this.endSignalled = false;
                }
            }

            foreach (var update in flushed)
            {
                receiver.Push(update);
            }

            if (failure is not null)
            {
                receiver.Fail(failure);
            }
            else if (completedSucceeded)
            {
                receiver.Complete();
            }
        }

        internal void Fail(Exception exception)
        {
            ICopilotSubAgentReceiver? target;
            lock (this.gate)
            {
                if (this.disposed)
                {
                    return;
                }

                this.completedFailure ??= exception;
                target = this.receiver;
                if (target is null)
                {
                    this.endSignalled = true;
                    return;
                }
            }

            target.Fail(exception);
        }

        internal async Task CompleteAsync()
        {
            ICopilotSubAgentReceiver? target;
            RunningAgentChatLease? leaseToDispose;
            lock (this.gate)
            {
                this.completedSucceeded = true;
                target = this.receiver;
                if (target is null)
                {
                    this.endSignalled = true;
                    this.disposed = true;
                    leaseToDispose = this.lease;
                    this.lease = null;
                }
                else
                {
                    leaseToDispose = this.lease;
                    this.lease = null;
                    this.disposed = true;
                }
            }

            if (target is not null)
            {
                target.Complete();
                if (leaseToDispose is { } lease && lease.AgentChat is { } agentChat)
                {
                    agentChat.SetCompletionState(AgentChatCompletionState.Succeeded);
                }
            }

            if (leaseToDispose is not null)
            {
                await leaseToDispose.DisposeAsync().ConfigureAwait(false);
            }
        }

        internal async Task CompleteAsFailedAsync(Exception exception)
        {
            ICopilotSubAgentReceiver? target;
            RunningAgentChatLease? leaseToDispose;
            lock (this.gate)
            {
                this.completedFailure ??= exception;
                target = this.receiver;
                leaseToDispose = this.lease;
                this.lease = null;
                this.disposed = true;
                if (target is null)
                {
                    this.endSignalled = true;
                }
            }

            if (target is not null)
            {
                target.Fail(exception);
                if (leaseToDispose is { } lease && lease.AgentChat is { } agentChat)
                {
                    agentChat.SetCompletionState(AgentChatCompletionState.Failed);
                }
            }

            if (leaseToDispose is not null)
            {
                await leaseToDispose.DisposeAsync().ConfigureAwait(false);
            }
        }

        internal async Task DisposeAsync(Exception cancellationReason)
        {
            ICopilotSubAgentReceiver? target;
            RunningAgentChatLease? leaseToDispose;
            lock (this.gate)
            {
                if (this.disposed)
                {
                    return;
                }

                this.disposed = true;
                target = this.receiver;
                leaseToDispose = this.lease;
                this.lease = null;
                this.pending.Clear();
            }

            if (target is not null)
            {
                target.Fail(cancellationReason);
                if (leaseToDispose is { } lease && lease.AgentChat is { } agentChat)
                {
                    agentChat.SetCompletionState(AgentChatCompletionState.Failed);
                }
            }

            if (leaseToDispose is not null)
            {
                await leaseToDispose.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
