using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels.Collections;
using Phantom.Workspaces.Agent.Gui.ViewModels.Visualization;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;

/// <summary>
/// Renders a single chat message as incremental HTML operations against an
/// <see cref="IChatOutputHtmlSink"/>. The full element is produced once via <see cref="BuildHtml"/>
/// (for insertion); subsequent <see cref="Update"/>/<see cref="Refresh"/> calls emit only the
/// per-content operations needed to reconcile the DOM, reusing stable element ids so unchanged nodes
/// are preserved.
/// </summary>
internal sealed class ChatMessageHtmlModel
{
    private sealed record ContentBinding(string Key, string ElementId, string Html);

    private readonly IChatOutputHtmlSink sink;
    private readonly Func<bool> isReasoningVisible;
    private readonly Func<bool>? isDiagnosticsVisible;
    private readonly IToolVisualizerFactory? toolFactory;
    private readonly IAgentStatusSink? statusSink;
    private readonly Func<string, string?>? resolveSubAgentId;
    private readonly List<ContentBinding> bindings = [];
    private AgentChatHistoryItem source;
    private string? renderedRoleLabel;
    private bool hasRendered;
    private bool lastReasoningVisible;
    private bool lastDiagnosticsVisible;
    private Dictionary<string, FunctionResultContent>? supplementalResults;

    public ChatMessageHtmlModel(
        string elementId,
        AgentChatHistoryItem source,
        Func<bool> isReasoningVisible,
        IChatOutputHtmlSink sink,
        Func<bool>? isDiagnosticsVisible = null,
        IToolVisualizerFactory? toolFactory = null,
        IAgentStatusSink? statusSink = null,
        Func<string, string?>? resolveSubAgentId = null)
    {
        ArgumentNullException.ThrowIfNull(isReasoningVisible);
        ArgumentNullException.ThrowIfNull(sink);
        this.ElementId = elementId;
        this.source = source;
        this.isReasoningVisible = isReasoningVisible;
        this.isDiagnosticsVisible = isDiagnosticsVisible;
        this.sink = sink;
        this.toolFactory = toolFactory;
        this.statusSink = statusSink;
        this.resolveSubAgentId = resolveSubAgentId;
        this.Render(emit: false);
    }

    public string ElementId { get; }

    /// <summary>Set once the message element has been inserted into the DOM by its transformer.</summary>
    public bool IsInserted { get; set; }

    /// <summary>
    /// Returns true if this message's source contains a <see cref="FunctionCallContent"/> with the
    /// given <paramref name="callId"/>. Used by the transformer to locate the matching call slot when
    /// a tool-result message arrives in a separate message.
    /// </summary>
    public bool HasCallWithId(string? callId)
    {
        if (callId is null)
        {
            return false;
        }

        foreach (var content in this.source.Contents)
        {
            if (content is FunctionCallContent call && call.CallId == callId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Injects a <see cref="FunctionResultContent"/> from a separate tool-role message into this
    /// message's rendering so the result appears nested under its matching call item. Triggers a
    /// re-render if the message has already been inserted into the DOM.
    /// </summary>
    public void AddSupplementalResult(FunctionResultContent result)
    {
        if (result.CallId is null)
        {
            return;
        }

        this.supplementalResults ??= new Dictionary<string, FunctionResultContent>(StringComparer.Ordinal);
        this.supplementalResults[result.CallId] = result;

        if (this.IsInserted)
        {
            this.Render(emit: true);
        }
    }

    /// <summary>Builds the full message element for initial insertion from the current bindings.</summary>
    public string BuildHtml()
    {
        var roleLabel = this.source.Role.Value;
        this.renderedRoleLabel = roleLabel;
        string? jumpLinkHtml = null;
        if (this.source.ParentToolCallId is { } parentToolCallId && this.resolveSubAgentId is not null)
        {
            var subAgentId = this.resolveSubAgentId(parentToolCallId);
            if (subAgentId is not null)
            {
                jumpLinkHtml = ChatOutputHtmlRenderer.RenderSubAgentJumpLink(subAgentId);
            }
        }

        return ChatOutputHtmlRenderer.RenderMessage(
            this.ElementId,
            roleLabel,
            this.bindings.Select(binding => (binding.ElementId, binding.Html)).ToList(),
            this.source.Timestamp,
            jumpLinkHtml);
    }

    public void Update(AgentChatHistoryItem newSource)
    {
        if (ReferenceEquals(newSource, this.source))
        {
            return;
        }

        this.source = newSource;
        this.Render(emit: true);
    }

    public void Refresh() => this.Render(emit: true);

    private void Render(bool emit)
    {
        var includeReasoning = this.isReasoningVisible();
        var includeDiagnostics = this.isDiagnosticsVisible?.Invoke() ?? true;
        var reasoningChanged = !this.hasRendered || includeReasoning != this.lastReasoningVisible;
        var diagnosticsChanged = !this.hasRendered || includeDiagnostics != this.lastDiagnosticsVisible;
        var visibilityChanged = reasoningChanged || diagnosticsChanged;

        var roleLabel = this.source.Role.Value;
        var isDiagnostic = string.Equals(
            roleLabel,
            AgentChatHistoryItem.DiagnosticChatRole.Value,
            StringComparison.OrdinalIgnoreCase);

        // Pre-scan: build a CallId → result lookup for content-level call+result pairing.
        Dictionary<string, FunctionResultContent>? resultLookup = null;
        foreach (var content in this.source.Contents)
        {
            if (content is FunctionResultContent result && result.CallId is not null)
            {
                resultLookup ??= new Dictionary<string, FunctionResultContent>(StringComparer.Ordinal);
                resultLookup.TryAdd(result.CallId, result);
            }
        }

        // Merge supplemental results injected from a separate tool-role message (cross-message pairing).
        if (this.supplementalResults is not null)
        {
            resultLookup ??= new Dictionary<string, FunctionResultContent>(StringComparer.Ordinal);
            foreach (var (callId, supplemental) in this.supplementalResults)
            {
                resultLookup.TryAdd(callId, supplemental);
            }
        }

        // Determine which result CallIds are "claimed" (have a matching call in this message).
        HashSet<string>? claimedResultCallIds = null;
        if (resultLookup is not null)
        {
            foreach (var content in this.source.Contents)
            {
                if (content is FunctionCallContent call && call.CallId is not null && resultLookup.ContainsKey(call.CallId))
                {
                    claimedResultCallIds ??= new HashSet<string>(StringComparer.Ordinal);
                    claimedResultCallIds.Add(call.CallId);
                }
            }
        }

        var newBindings = new List<ContentBinding>(this.source.Contents.Count);
        var contentIndex = 0;
        var contentCount = this.source.Contents.Count;

        while (contentIndex < contentCount)
        {
            var content = this.source.Contents[contentIndex];

            if (content is FunctionCallContent firstCall)
            {
                // Collect the maximal run of consecutive FunctionCallContent items.
                var calls = new List<FunctionCallContent> { firstCall };
                contentIndex++;
                while (contentIndex < contentCount && this.source.Contents[contentIndex] is FunctionCallContent nextCall)
                {
                    calls.Add(nextCall);
                    contentIndex++;
                }

                var elementId = ChatOutputHtmlRenderer.ContentId(this.ElementId, newBindings.Count);

                // Composite key: concatenation of each call's key and its matched result's key.
                var keyParts = new List<string>(calls.Count * 2);
                foreach (var call in calls)
                {
                    keyParts.Add(ChatOutputHtmlRenderer.ComputeContentKey(call, isDiagnostic));
                    if (call.CallId is not null && resultLookup is not null && resultLookup.TryGetValue(call.CallId, out var matchedResult))
                    {
                        keyParts.Add(ChatOutputHtmlRenderer.ComputeContentKey(matchedResult, isDiagnostic));
                    }
                }

                var groupKey = "group:" + string.Join("\x02", keyParts);
                var groupHtml = ChatOutputHtmlRenderer.RenderToolGroup(elementId, calls, resultLookup);
                newBindings.Add(new ContentBinding(groupKey, elementId, groupHtml));
                continue;
            }

            // Skip FunctionResultContent items claimed by a tool group.
            if (content is FunctionResultContent claimedResult &&
                claimedResult.CallId is not null &&
                claimedResultCallIds is not null &&
                claimedResultCallIds.Contains(claimedResult.CallId))
            {
                contentIndex++;
                continue;
            }

            if (isDiagnostic && !includeDiagnostics && content is TextContent)
            {
                contentIndex++;
                continue;
            }

            var contentId = ChatOutputHtmlRenderer.ContentId(this.ElementId, newBindings.Count);
            var html = ChatOutputHtmlRenderer.RenderContent(contentId, content, includeReasoning, isDiagnostic, this.toolFactory, this.statusSink);
            if (html is not null)
            {
                var key = ChatOutputHtmlRenderer.ComputeContentKey(content, isDiagnostic);
                newBindings.Add(new ContentBinding(key, contentId, html));
            }

            contentIndex++;
        }

        if (emit)
        {
            this.EmitDiff(newBindings, roleLabel, visibilityChanged);
        }

        this.bindings.Clear();
        this.bindings.AddRange(newBindings);
        this.hasRendered = true;
        this.lastReasoningVisible = includeReasoning;
        this.lastDiagnosticsVisible = includeDiagnostics;
        this.renderedRoleLabel = roleLabel;
    }

    private void EmitDiff(List<ContentBinding> newBindings, string roleLabel, bool visibilityChanged)
    {
        if (!string.Equals(this.renderedRoleLabel, roleLabel, StringComparison.Ordinal))
        {
            this.sink.UpdateContent(
                ChatOutputHtmlRenderer.HeaderId(this.ElementId),
                ChatOutputUpdateLocation.Replace,
                ChatOutputHtmlRenderer.RenderHeader(this.ElementId, roleLabel));
        }

        for (var index = 0; index < newBindings.Count; index++)
        {
            if (index < this.bindings.Count)
            {
                if (!visibilityChanged && this.bindings[index].Key == newBindings[index].Key)
                {
                    continue;
                }

                this.sink.UpdateContent(newBindings[index].ElementId, ChatOutputUpdateLocation.Replace, newBindings[index].Html);
            }
            else
            {
                this.sink.UpdateContent(
                    ChatOutputHtmlRenderer.ContentsContainerId(this.ElementId),
                    ChatOutputUpdateLocation.Append,
                    newBindings[index].Html);
            }
        }

        for (var index = newBindings.Count; index < this.bindings.Count; index++)
        {
            this.sink.RemoveContent(this.bindings[index].ElementId);
        }
    }
}

/// <summary>
/// Renders a run of consecutive tool-call/result history items as a single collapsible
/// <c>details</c> element. Children are <see cref="ChatMessageHtmlModel"/> instances whose HTML
/// is placed inside the expanded body. The summary line shows the last tool name and a call-count badge.
/// </summary>
internal sealed class ToolCallGroupHtmlModel
{
    private readonly IChatOutputHtmlSink sink;
    private readonly string groupId;
    private string lastToolName;
    private int callCount;

    public ToolCallGroupHtmlModel(string groupId, IChatOutputHtmlSink sink, string firstToolName)
    {
        this.groupId = groupId;
        this.sink = sink;
        this.lastToolName = firstToolName;
        this.callCount = 1;
    }

    public string GroupId => this.groupId;

    /// <summary>
    /// Builds the complete group element for DOM insertion, with <paramref name="firstMessageHtml"/>
    /// pre-placed inside the body container.
    /// </summary>
    public string BuildHtml(string firstMessageHtml)
        => ChatOutputHtmlRenderer.RenderToolCallGroup(this.groupId, this.lastToolName, this.callCount, firstMessageHtml);

    /// <summary>Appends <paramref name="model"/> to the group and updates the summary badge.</summary>
    public void AppendItem(ChatMessageHtmlModel model, string toolName)
    {
        this.callCount++;
        this.lastToolName = toolName;

        this.sink.UpdateContent(
            ChatOutputHtmlRenderer.ToolCallGroupBodyId(this.groupId),
            ChatOutputUpdateLocation.Append,
            model.BuildHtml());
        model.IsInserted = true;

        this.sink.UpdateContent(
            ChatOutputHtmlRenderer.ToolCallGroupSummaryId(this.groupId),
            ChatOutputUpdateLocation.Replace,
            ChatOutputHtmlRenderer.RenderToolCallGroupSummary(this.groupId, this.lastToolName, this.callCount));
    }

    /// <summary>
    /// Updates group state (call count, last tool name, and <see cref="ChatMessageHtmlModel.IsInserted"/>)
    /// without emitting any sink operations. Used during initial population for items already in the DOM
    /// (rendered by the chunk loader, so DOM calls are suppressed via <c>skipInitialItems</c>).
    /// </summary>
    internal void AppendItemStateOnly(ChatMessageHtmlModel model, string toolName)
    {
        this.callCount++;
        this.lastToolName = toolName;
        model.IsInserted = true;
    }
}

/// <summary>
/// Discriminated render target for <see cref="ChatMessageHtmlTransformer"/>. Each source item maps
/// to a model plus an optional group; the group is set when the item belongs to a tool-call run.
/// </summary>
internal sealed class RenderSlot
{
    public RenderSlot(ChatMessageHtmlModel model) => this.Model = model;

    public ChatMessageHtmlModel Model { get; }

    /// <summary>Non-null when this item has been merged into a tool-call group.</summary>
    public ToolCallGroupHtmlModel? Group { get; set; }
}

/// <summary>
/// Transforms a chat-message source collection into <see cref="ChatMessageHtmlModel"/> instances,
/// emitting insertion/removal operations against a parent container in the DOM.
/// </summary>
internal sealed class ChatMessageHtmlTransformer : CollectionTransformer<AgentChatHistoryItem, RenderSlot>
{
    private readonly IChatOutputHtmlSink sink;
    private readonly Func<bool> isReasoningVisible;
    private readonly Func<bool>? isDiagnosticsVisible;
    private readonly Func<int> nextId;
    private readonly string containerPath;
    private readonly IToolVisualizerFactory? toolFactory;
    private readonly IAgentStatusSink? statusSink;
    private readonly Func<string, string?>? resolveSubAgentId;
    private readonly int skipInitialItems;
    private bool inInitialTransform;

    public ChatMessageHtmlTransformer(
        IReadOnlyList<AgentChatHistoryItem> source,
        List<RenderSlot> target,
        IChatOutputHtmlSink sink,
        Func<bool> isReasoningVisible,
        Func<int> nextId,
        string containerPath,
        Func<bool>? isDiagnosticsVisible = null,
        IToolVisualizerFactory? toolFactory = null,
        IAgentStatusSink? statusSink = null,
        Func<string, string?>? resolveSubAgentId = null,
        int skipInitialItems = 0)
        : base(source, target)
    {
        this.sink = sink;
        this.isReasoningVisible = isReasoningVisible;
        this.isDiagnosticsVisible = isDiagnosticsVisible;
        this.nextId = nextId;
        this.containerPath = containerPath;
        this.toolFactory = toolFactory;
        this.statusSink = statusSink;
        this.resolveSubAgentId = resolveSubAgentId;
        this.skipInitialItems = skipInitialItems;
        this.inInitialTransform = true;
        this.ApplyInitialTransform();
        this.inInitialTransform = false;
    }

    protected override RenderSlot Create(AgentChatHistoryItem sourceItem)
        => new(new ChatMessageHtmlModel(ChatOutputHtmlRenderer.MessageId(this.nextId()), sourceItem, this.isReasoningVisible, this.sink, this.isDiagnosticsVisible, this.toolFactory, this.statusSink, this.resolveSubAgentId));

    protected override void Update(RenderSlot target, AgentChatHistoryItem sourceItem)
        => target.Model.Update(sourceItem);

    protected override void OnInsert(int index, RenderSlot slot)
    {
        var sourceItem = this.Source[index];
        var suppressSink = this.inInitialTransform && index < this.skipInitialItems;

        // If the new item contains only FunctionResultContent items, try to inject each result into
        // the preceding slot that owns the matching FunctionCallContent. When any result is injected
        // the tool-result message produces no DOM element of its own (the result is shown nested
        // inside the call item). Unmatched results fall through to normal standalone rendering.
        if (IsToolResultOnlyItem(sourceItem))
        {
            var anyInjected = false;
            foreach (var content in sourceItem.Contents)
            {
                if (content is FunctionResultContent result)
                {
                    var matchSlot = this.FindSlotWithCallId(result.CallId);
                    if (matchSlot is not null)
                    {
                        matchSlot.Model.AddSupplementalResult(result);
                        anyInjected = true;
                    }
                }
            }

            if (anyInjected)
            {
                // Results injected; no DOM element for this message.
                return;
            }

            // No matching calls found; fall through to standalone rendering below.
        }

        if (IsToolCallOnlyItem(sourceItem))
        {
            var toolName = GetLastToolName(sourceItem);

            var prevIndex = this.FindPrecedingToolCallSlotIndex(index);
            if (prevIndex >= 0)
            {
                var prevSlot = this.Target[prevIndex];

                if (prevSlot.Group is { } existingGroup)
                {
                    // Extend the existing group: no new top-level DOM element needed.
                    if (!suppressSink)
                        existingGroup.AppendItem(slot.Model, toolName);
                    else
                        existingGroup.AppendItemStateOnly(slot.Model, toolName);
                    slot.Group = existingGroup;
                    return;
                }

                if (IsToolCallOnlyItem(this.Source[prevIndex]))
                {
                    // Previous item was a standalone tool call: promote both into a new group.
                    var groupId = ChatOutputHtmlRenderer.ToolCallGroupId(this.nextId());
                    var prevToolName = GetLastToolName(this.Source[prevIndex]);
                    var group = new ToolCallGroupHtmlModel(groupId, this.sink, prevToolName);

                    // Replace the previous standalone message with the group that wraps it.
                    if (!suppressSink)
                        this.sink.UpdateContent(
                            prevSlot.Model.ElementId,
                            ChatOutputUpdateLocation.Replace,
                            group.BuildHtml(prevSlot.Model.BuildHtml()));
                    prevSlot.Group = group;

                    if (!suppressSink)
                        group.AppendItem(slot.Model, toolName);
                    else
                        group.AppendItemStateOnly(slot.Model, toolName);
                    slot.Group = group;
                    return;
                }
            }
        }

        // Standalone insert (non-tool-call, or first/isolated tool call with no adjacent group).
        if (!suppressSink)
        {
            var (location, reference) = ChatOutputHtmlInsertion.ResolveInsertTarget(
                this.Target,
                index,
                this.containerPath,
                static s => s.Model.IsInserted,
                static s => s.Group?.GroupId ?? s.Model.ElementId);
            this.sink.UpdateContent(reference, location, slot.Model.BuildHtml());
        }

        // Mark as inserted in both the live path and the skip path (Phase B already put it in the DOM).
        slot.Model.IsInserted = true;
    }

    protected override void OnRemoveAt(int index, RenderSlot slot)
        => this.sink.RemoveContent(slot.Model.ElementId);

    /// <summary>
    /// Returns true when the item contains only <see cref="FunctionResultContent"/> items.
    /// Such messages are handled by cross-message result injection rather than message-level grouping.
    /// </summary>
    private static bool IsToolResultOnlyItem(AgentChatHistoryItem item)
    {
        if (item.Contents.Count == 0)
        {
            return false;
        }

        foreach (var content in item.Contents)
        {
            if (content is not FunctionResultContent)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsToolCallOnlyItem(AgentChatHistoryItem item)
    {
        if (item.Contents.Count == 0)
        {
            return false;
        }

        foreach (var content in item.Contents)
        {
            if (content is not FunctionCallContent)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Searches backwards through <see cref="CollectionTransformer{TSource,TTarget}.Target"/> for
    /// the most recent slot whose source message contains a <see cref="FunctionCallContent"/> with
    /// the given <paramref name="callId"/>. Returns <see langword="null"/> if not found.
    /// </summary>
    private RenderSlot? FindSlotWithCallId(string? callId)
    {
        if (callId is null)
        {
            return null;
        }

        for (var i = this.Target.Count - 1; i >= 0; i--)
        {
            if (this.Target[i].Model.HasCallWithId(callId))
            {
                return this.Target[i];
            }
        }

        return null;
    }

    /// <summary>
    /// Searches backwards from <paramref name="index"/> - 1, skipping tool-result-only messages,
    /// and returns the index of the most recent source item that is either a tool-call-only message
    /// or already belongs to a group. Returns -1 if the search reaches the start of the collection
    /// or hits any other kind of message.
    /// </summary>
    private int FindPrecedingToolCallSlotIndex(int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (IsToolResultOnlyItem(this.Source[i]))
            {
                continue;
            }

            if (IsToolCallOnlyItem(this.Source[i]) || this.Target[i].Group is not null)
            {
                return i;
            }

            return -1;
        }

        return -1;
    }

    private static string GetLastToolName(AgentChatHistoryItem item)
    {
        for (var i = item.Contents.Count - 1; i >= 0; i--)
        {
            if (item.Contents[i] is FunctionCallContent call)
            {
                return call.Name ?? string.Empty;
            }
        }

        return string.Empty;
    }
}

/// <summary>Shared logic for choosing where a newly inserted element is placed in the DOM.</summary>
internal static class ChatOutputHtmlInsertion
{
    /// <summary>
    /// Resolves the placement for the item at <paramref name="index"/> (already present in
    /// <paramref name="target"/>). Prefers <c>After</c> the previous already-inserted sibling, then
    /// <c>Before</c> the next already-inserted sibling, otherwise appends into the container. This
    /// keeps both the sequential initial population and incremental inserts correct, never referencing
    /// a sibling that is not yet in the DOM.
    /// </summary>
    public static (ChatOutputUpdateLocation Location, string Reference) ResolveInsertTarget<T>(
        IList<T> target,
        int index,
        string containerPath,
        Func<T, bool> isInserted,
        Func<T, string> elementId)
    {
        if (index - 1 >= 0 && isInserted(target[index - 1]))
        {
            return (ChatOutputUpdateLocation.After, elementId(target[index - 1]));
        }

        if (index + 1 < target.Count && isInserted(target[index + 1]))
        {
            return (ChatOutputUpdateLocation.Before, elementId(target[index + 1]));
        }

        return (ChatOutputUpdateLocation.Append, containerPath);
    }
}

/// <summary>Renders a single running (in-progress) turn: an empty container that hosts its streaming messages.</summary>
internal sealed class RunningChatItemHtmlModel : IDisposable
{
    private readonly IChatOutputHtmlSink sink;
    private readonly Func<bool> isReasoningVisible;
    private readonly Func<bool>? isDiagnosticsVisible;
    private readonly Func<int> nextId;
    private readonly IToolVisualizerFactory? toolFactory;
    private readonly IAgentStatusSink? statusSink;
    private readonly List<RenderSlot> messageSlots = [];
    private ChatMessageHtmlTransformer? transformer;

    public RunningChatItemHtmlModel(
        string elementId,
        AgentChatRunningItem source,
        Func<bool> isReasoningVisible,
        IChatOutputHtmlSink sink,
        Func<int> nextId,
        Func<bool>? isDiagnosticsVisible = null,
        IToolVisualizerFactory? toolFactory = null,
        IAgentStatusSink? statusSink = null)
    {
        ArgumentNullException.ThrowIfNull(isReasoningVisible);
        ArgumentNullException.ThrowIfNull(sink);
        this.ElementId = elementId;
        this.Source = source;
        this.isReasoningVisible = isReasoningVisible;
        this.isDiagnosticsVisible = isDiagnosticsVisible;
        this.sink = sink;
        this.nextId = nextId;
        this.toolFactory = toolFactory;
        this.statusSink = statusSink;
    }

    public string ElementId { get; }

    public bool IsInserted { get; set; }

    public AgentChatRunningItem Source { get; private set; }

    /// <summary>The empty container element; messages are appended into it once it is activated.</summary>
    public string BuildHtml() => ChatOutputHtmlRenderer.RenderRunningItemContainer(this.ElementId);

    /// <summary>
    /// Builds the inner message transformer, which appends the current and future messages into the
    /// (now inserted) container. Must be called after the container element has been inserted.
    /// </summary>
    public void Activate()
    {
        this.transformer = new ChatMessageHtmlTransformer(
            this.Source.Items,
            this.messageSlots,
            this.sink,
            this.isReasoningVisible,
            this.nextId,
            ChatOutputHtmlRenderer.RunningItemContentsId(this.ElementId),
            this.isDiagnosticsVisible,
            this.toolFactory,
            this.statusSink);
    }

    public void Update(AgentChatRunningItem source)
    {
        var previousItems = this.Source.Items;
        this.Source = source;

        if (ReferenceEquals(previousItems, source.Items) || this.transformer is null)
        {
            return;
        }

        this.transformer.Dispose();
        this.messageSlots.Clear();
        this.sink.UpdateContent(
            ChatOutputHtmlRenderer.RunningItemContentsId(this.ElementId),
            ChatOutputUpdateLocation.Replace,
            $"<div class=\"chat-running-contents\" id=\"{ChatOutputHtmlRenderer.RunningItemContentsId(this.ElementId)}\"></div>");
        this.Activate();
    }

    public void Refresh()
    {
        foreach (var slot in this.messageSlots)
        {
            slot.Model.Refresh();
        }
    }

    /// <summary>
    /// Re-establishes the insertion point after a DOM failure by discarding the current transformer,
    /// clearing message slots, and re-inserting the container via <c>Append</c> into
    /// <paramref name="containerPath"/> — a location that is always reachable (e.g. the static
    /// running-items region).  Activates a fresh inner transformer so subsequent streaming chunks
    /// arrive into the newly-placed contents div.
    /// </summary>
    public void ReInsert(string containerPath)
    {
        this.IsInserted = false;
        this.transformer?.Dispose();
        this.transformer = null;
        this.messageSlots.Clear();
        this.sink.UpdateContent(containerPath, ChatOutputUpdateLocation.Append, this.BuildHtml());
        this.IsInserted = true;
        this.Activate();
    }

    public void Dispose() => this.transformer?.Dispose();
}

/// <summary>Transforms the running-items source collection into <see cref="RunningChatItemHtmlModel"/> instances.</summary>
internal sealed class RunningChatItemsHtmlTransformer : CollectionTransformer<AgentChatRunningItem, RunningChatItemHtmlModel>
{
    private readonly IChatOutputHtmlSink sink;
    private readonly Func<bool> isReasoningVisible;
    private readonly Func<bool>? isDiagnosticsVisible;
    private readonly Func<int> nextId;
    private readonly IToolVisualizerFactory? toolFactory;
    private readonly IAgentStatusSink? statusSink;

    public RunningChatItemsHtmlTransformer(
        IReadOnlyList<AgentChatRunningItem> source,
        List<RunningChatItemHtmlModel> target,
        IChatOutputHtmlSink sink,
        Func<bool> isReasoningVisible,
        Func<int> nextId,
        Func<bool>? isDiagnosticsVisible = null,
        IToolVisualizerFactory? toolFactory = null,
        IAgentStatusSink? statusSink = null)
        : base(source, target)
    {
        this.sink = sink;
        this.isReasoningVisible = isReasoningVisible;
        this.isDiagnosticsVisible = isDiagnosticsVisible;
        this.nextId = nextId;
        this.toolFactory = toolFactory;
        this.statusSink = statusSink;
        this.ApplyInitialTransform();
    }

    public IReadOnlyList<RunningChatItemHtmlModel> Models => (List<RunningChatItemHtmlModel>)this.Target;

    protected override RunningChatItemHtmlModel Create(AgentChatRunningItem sourceItem)
        => new(ChatOutputHtmlRenderer.RunningItemId(this.nextId()), sourceItem, this.isReasoningVisible, this.sink, this.nextId, this.isDiagnosticsVisible, this.toolFactory, this.statusSink);

    protected override void Update(RunningChatItemHtmlModel target, AgentChatRunningItem sourceItem)
        => target.Update(sourceItem);

    protected override void OnInsert(int index, RunningChatItemHtmlModel target)
    {
        var (location, reference) = ChatOutputHtmlInsertion.ResolveInsertTarget(
            this.Target,
            index,
            ChatOutputHtmlRenderer.RunningContainerId,
            static model => model.IsInserted,
            static model => model.ElementId);
        this.sink.UpdateContent(reference, location, target.BuildHtml());
        target.IsInserted = true;
        target.Activate();
    }

    protected override void OnRemoveAt(int index, RunningChatItemHtmlModel target)
        => this.sink.RemoveContent(target.ElementId);
}

/// <summary>
/// Top-level chat-output model for the browser-hosted renderer. Mirrors the selectable-text output
/// model but, instead of building an Avalonia inline tree, emits incremental HTML operations to an
/// <see cref="IChatOutputHtmlSink"/> and requests a scroll-to-bottom after each content change.
/// History is loaded asynchronously in three phases to keep the UI thread responsive.
/// </summary>
public sealed class ChatOutputHtmlModel : IDisposable
{
    /// <summary>Maximum number of history items processed in a single off-thread generation chunk.</summary>
    public const int HistoryChunkSize = 200;

    private readonly IChatOutputHtmlSink sink;
    private readonly IReadOnlyList<AgentChatHistoryItem> historyItems;
    private readonly IReadOnlyList<AgentChatRunningItem> runningItems;
    private readonly Func<bool> isReasoningVisible;
    private readonly Func<bool>? isDiagnosticsVisible;
    private readonly IToolVisualizerFactory? toolFactory;
    private readonly IAgentStatusSink? statusSink;
    private readonly Func<string, string?>? resolveSubAgentId;
    private readonly RunningChatItemsHtmlTransformer runningTransformer;
    private readonly RunningSubAgentsHtmlTransformer? subAgentsTransformer;
    private readonly List<RenderSlot> historySlots = [];
    private readonly List<RunningChatItemHtmlModel> runningModels = [];
    private readonly Dictionary<AgentChatRunningItem, NotifyCollectionChangedEventHandler> runningItemHandlers = [];
    private readonly CancellationTokenSource loadCts = new();
    private ChatMessageHtmlTransformer? historyTransformer;
    private bool historyLoading;
    private List<NotifyCollectionChangedEventArgs>? bufferedHistoryEvents;
    private int idSequence;

    /// <summary>
    /// Task that completes when the history has been fully loaded and the live transformer is ready.
    /// Exposed internally for tests to await before asserting history-related sink state.
    /// </summary>
    internal Task HistoryLoaded { get; }

    public ChatOutputHtmlModel(
        IReadOnlyList<AgentChatHistoryItem> historyItems,
        IReadOnlyList<AgentChatRunningItem> runningItems,
        Func<bool> isReasoningVisible,
        IChatOutputHtmlSink sink,
        Func<bool>? isDiagnosticsVisible = null,
        IToolVisualizerFactory? toolFactory = null,
        IAgentStatusSink? statusSink = null,
        Func<string, string?>? resolveSubAgentId = null,
        IReadOnlyList<IRunningSubAgentDisplay>? subAgents = null,
        IReadOnlyList<IRunningSubAgent>? ancestors = null)
    {
        ArgumentNullException.ThrowIfNull(historyItems);
        ArgumentNullException.ThrowIfNull(runningItems);
        ArgumentNullException.ThrowIfNull(isReasoningVisible);
        ArgumentNullException.ThrowIfNull(sink);

        this.historyItems = historyItems;
        this.runningItems = runningItems;
        this.sink = sink;
        this.isReasoningVisible = isReasoningVisible;
        this.isDiagnosticsVisible = isDiagnosticsVisible;
        this.toolFactory = toolFactory;
        this.statusSink = statusSink;
        this.resolveSubAgentId = resolveSubAgentId;

        // Phase A: take a snapshot of history for off-thread processing.
        var snapshot = new List<AgentChatHistoryItem>(historyItems);
        this.historyLoading = true;
        this.bufferedHistoryEvents = [];

        // Subscribe before Task.Run so no CollectionChanged events are missed.
        if (historyItems is INotifyCollectionChanged historyChanged)
        {
            historyChanged.CollectionChanged += this.OnHistoryCollectionChanged;
        }

        this.runningTransformer = new RunningChatItemsHtmlTransformer(
            runningItems,
            this.runningModels,
            sink,
            isReasoningVisible,
            this.NextId,
            isDiagnosticsVisible,
            toolFactory,
            statusSink);

        if (subAgents is not null)
        {
            this.subAgentsTransformer = new RunningSubAgentsHtmlTransformer(subAgents, ancestors ?? [], sink);
        }

        if (runningItems is INotifyCollectionChanged runningChanged)
        {
            runningChanged.CollectionChanged += this.OnRunningCollectionChanged;
        }

        this.SyncRunningItemSubscriptions();

        // Fire off background history load; HistoryLoaded completes when Phase C finishes.
        this.HistoryLoaded = Task.Run(() => this.LoadHistoryChunksAsync(snapshot, this.loadCts.Token));
    }

    private async Task LoadHistoryChunksAsync(
        List<AgentChatHistoryItem> snapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            var chunks = ComputeChunkRanges(snapshot);
            var idBox = new int[1];

            // Process chunks oldest-first so that each chunk is appended in DOM order
            // and the integer IDs assigned by idBox match the index-order IDs that the
            // live ChatMessageHtmlTransformer will assign in Phase C.
            for (var chunkIndex = chunks.Count - 1; chunkIndex >= 0; chunkIndex--)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (start, end) = chunks[chunkIndex];
                var chunkSlice = snapshot.GetRange(start, end - start);
                var (cmds, _) = GenerateHistoryChunk(
                    chunkSlice, idBox,
                    this.isReasoningVisible, this.isDiagnosticsVisible,
                    this.toolFactory, this.statusSink, this.resolveSubAgentId);

                var isLastChunk = chunkIndex == 0;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    this.sink.BeginBatch();

                    foreach (var cmd in cmds)
                    {
                        if (cmd.Location is null)
                        {
                            this.sink.RemoveContent(cmd.Path);
                        }
                        else
                        {
                            this.sink.UpdateContent(cmd.Path, cmd.Location.Value, cmd.Content!);
                        }
                    }

                    if (isLastChunk)
                    {
                        this.sink.ScrollToBottom();
                    }

                    this.sink.EndBatch();
                });
            }

            // Phase C: construct live transformer and replay any buffered events.
            var finalIdBox = idBox;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                // Reset id sequence to 0 so ApplyInitialTransform assigns the same integer IDs to
                // pre-existing history slots as Phase B did (which also started from 0, oldest-first).
                // We save the running-items count to restore it afterward, preventing collisions.
                var savedIdSequence = this.idSequence;
                this.idSequence = 0;

                // Construct the live transformer. ApplyInitialTransform skips items 0..snapshot.Count-1
                // (rendered by chunks) and emits DOM ops for any items added to historyItems during load.
                this.historyTransformer = new ChatMessageHtmlTransformer(
                    this.historyItems,
                    this.historySlots,
                    this.sink,
                    this.isReasoningVisible,
                    this.NextId,
                    ChatOutputHtmlRenderer.HistoryContainerId,
                    this.isDiagnosticsVisible,
                    this.toolFactory,
                    this.statusSink,
                    this.resolveSubAgentId,
                    skipInitialItems: snapshot.Count);

                // Ensure future ids don't collide with running-item ids allocated in Phase A.
                this.idSequence = Math.Max(savedIdSequence, this.idSequence);

                this.historyLoading = false;

                if (this.bufferedHistoryEvents is { Count: > 0 })
                {
                    this.sink.ScrollToBottom();
                }

                this.bufferedHistoryEvents = null;
            });
        }
        catch (OperationCanceledException)
        {
            // Disposed before loading completed; no further action needed.
        }
    }

    /// <summary>Re-renders every message (for example, when reasoning visibility toggles).</summary>
    public void Refresh()
    {
        if (!this.historyLoading)
        {
            foreach (var slot in this.historySlots)
            {
                slot.Model.Refresh();
            }
        }

        foreach (var model in this.runningModels)
        {
            model.Refresh();
        }

        this.sink.ScrollToBottom();
    }

    /// <summary>
    /// Called when the browser reports that a DOM command targeting
    /// <paramref name="failedPath"/> was silently dropped because the element did not exist.
    /// Finds the affected running-item model (by its element id or its contents-div id) and
    /// calls <see cref="RunningChatItemHtmlModel.ReInsert"/> to recover the insertion point
    /// using a stable <c>Append</c> fallback.
    /// </summary>
    public void NotifyInsertionFailed(string failedPath)
    {
        foreach (var model in this.runningModels)
        {
            if (model.ElementId == failedPath ||
                ChatOutputHtmlRenderer.RunningItemContentsId(model.ElementId) == failedPath)
            {
                model.ReInsert(ChatOutputHtmlRenderer.RunningContainerId);
                return;
            }
        }
    }

    /// <summary>
    /// Generates HTML commands for a slice of history items, callable off the UI thread.
    /// Creates a <see cref="RecordingChatOutputHtmlSink"/>, runs a <see cref="ChatMessageHtmlTransformer"/>
    /// over <paramref name="chunk"/>, and returns the recorded commands together with the id of the
    /// first top-level element the transformer inserted into <see cref="ChatOutputHtmlRenderer.HistoryContainerId"/>.
    /// </summary>
    /// <param name="chunk">A read-only slice of history items to render.</param>
    /// <param name="idBox">
    /// A single-element array used as a shared mutable id counter. <c>idBox[0]</c> is read and
    /// incremented by the id factory so successive chunk calls advance a global counter without
    /// id collisions across chunks.
    /// </param>
    /// <param name="isReasoningVisible">Controls whether reasoning content is included.</param>
    /// <param name="isDiagnosticsVisible">Controls whether diagnostic content is included; null means always visible.</param>
    /// <param name="toolFactory">Optional tool visualizer factory; may be null.</param>
    /// <param name="statusSink">Optional agent-status sink; may be null.</param>
    /// <returns>
    /// The recorded <see cref="SinkCommand"/> list and the element id of the first top-level DOM node
    /// inserted by the transformer (<see langword="null"/> when <paramref name="chunk"/> is empty).
    /// </returns>
    internal static (IReadOnlyList<SinkCommand> Commands, string? FirstElementId)
        GenerateHistoryChunk(
            IReadOnlyList<AgentChatHistoryItem> chunk,
            int[] idBox,
            Func<bool> isReasoningVisible,
            Func<bool>? isDiagnosticsVisible,
            IToolVisualizerFactory? toolFactory,
            IAgentStatusSink? statusSink,
            Func<string, string?>? resolveSubAgentId = null)
    {
        var recording = new RecordingChatOutputHtmlSink();
        var slots = new List<RenderSlot>();
        using var transformer = new ChatMessageHtmlTransformer(
            chunk, slots, recording,
            isReasoningVisible, () => idBox[0]++,
            ChatOutputHtmlRenderer.HistoryContainerId,
            isDiagnosticsVisible, toolFactory, statusSink, resolveSubAgentId);

        string? firstElementId = slots.Count > 0
            ? (slots[0].Group?.GroupId ?? slots[0].Model.ElementId)
            : null;

        return (recording.Commands, firstElementId);
    }

    /// <summary>
    /// Returns a list of <c>(Start, End)</c> index ranges for chunks of <paramref name="snapshot"/>,
    /// newest-first. Each raw cut point (a multiple of <see cref="HistoryChunkSize"/> from the end)
    /// is snapped backward past any contiguous tool-related run it falls inside, ensuring that tool-call
    /// groups and their results are never split across independently generated chunks.
    ///
    /// <para>In pathological cases (e.g. a conversation consisting entirely of tool calls), snapping may
    /// produce a single chunk covering the entire snapshot. This is intentional: the whole run must be
    /// processed together for correct grouping.</para>
    /// </summary>
    internal static IReadOnlyList<(int Start, int End)> ComputeChunkRanges(
        IReadOnlyList<AgentChatHistoryItem> snapshot)
    {
        var chunks = new List<(int Start, int End)>();
        var i = snapshot.Count;
        while (i > 0)
        {
            var rawStart = Math.Max(0, i - HistoryChunkSize);
            var start = SnapCutPoint(snapshot, rawStart);
            chunks.Add((start, i));
            i = start;
        }

        return chunks;
    }

    private static int SnapCutPoint(IReadOnlyList<AgentChatHistoryItem> snapshot, int rawCut)
    {
        var k = rawCut;
        while (k > 0 && IsToolRelated(snapshot[k - 1]))
        {
            k--;
        }

        return k;
    }

    private static bool IsToolRelated(AgentChatHistoryItem item)
    {
        if (item.Contents.Count == 0)
        {
            return false;
        }

        var allCalls = true;
        var allResults = true;
        foreach (var content in item.Contents)
        {
            if (content is not FunctionCallContent) { allCalls = false; }
            if (content is not FunctionResultContent) { allResults = false; }
        }

        return allCalls || allResults;
    }

    public void Dispose()
    {
        this.loadCts.Cancel();
        this.loadCts.Dispose();

        if (this.historyItems is INotifyCollectionChanged historyChanged)
        {
            historyChanged.CollectionChanged -= this.OnHistoryCollectionChanged;
        }

        if (this.runningItems is INotifyCollectionChanged runningChanged)
        {
            runningChanged.CollectionChanged -= this.OnRunningCollectionChanged;
        }

        foreach (var pair in this.runningItemHandlers)
        {
            pair.Key.Items.CollectionChanged -= pair.Value;
        }

        this.runningItemHandlers.Clear();
        this.subAgentsTransformer?.Dispose();
        this.runningTransformer.Dispose();
        this.historyTransformer?.Dispose();
    }

    private int NextId() => this.idSequence++;

    private void OnHistoryCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (this.historyLoading)
        {
            this.bufferedHistoryEvents?.Add(e);
        }
        else
        {
            this.sink.ScrollToBottom();
        }
    }

    private void OnRunningCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        this.SyncRunningItemSubscriptions();
        this.sink.ScrollToBottom();
    }

    private void OnRunningItemMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => this.sink.ScrollToBottom();

    private void SyncRunningItemSubscriptions()
    {
        var removedItems = this.runningItemHandlers.Keys.Except(this.runningItems).ToArray();
        foreach (var removedItem in removedItems)
        {
            removedItem.Items.CollectionChanged -= this.runningItemHandlers[removedItem];
            this.runningItemHandlers.Remove(removedItem);
        }

        foreach (var runningItem in this.runningItems)
        {
            if (this.runningItemHandlers.ContainsKey(runningItem))
            {
                continue;
            }

            NotifyCollectionChangedEventHandler handler = this.OnRunningItemMessagesChanged;
            runningItem.Items.CollectionChanged += handler;
            this.runningItemHandlers[runningItem] = handler;
        }
    }
}
