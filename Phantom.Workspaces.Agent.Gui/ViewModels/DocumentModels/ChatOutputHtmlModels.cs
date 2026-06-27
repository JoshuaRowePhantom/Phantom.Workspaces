using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
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
    private readonly IToolVisualizerFactory? toolFactory;
    private readonly IAgentStatusSink? statusSink;
    private readonly List<ContentBinding> bindings = [];
    private AgentChatHistoryItem source;
    private string? renderedRoleLabel;
    private bool hasRendered;
    private bool lastReasoningVisible;

    public ChatMessageHtmlModel(
        string elementId,
        AgentChatHistoryItem source,
        Func<bool> isReasoningVisible,
        IChatOutputHtmlSink sink,
        IToolVisualizerFactory? toolFactory = null,
        IAgentStatusSink? statusSink = null)
    {
        ArgumentNullException.ThrowIfNull(isReasoningVisible);
        ArgumentNullException.ThrowIfNull(sink);
        this.ElementId = elementId;
        this.source = source;
        this.isReasoningVisible = isReasoningVisible;
        this.sink = sink;
        this.toolFactory = toolFactory;
        this.statusSink = statusSink;
        this.Render(emit: false);
    }

    public string ElementId { get; }

    /// <summary>Set once the message element has been inserted into the DOM by its transformer.</summary>
    public bool IsInserted { get; set; }

    /// <summary>Builds the full message element for initial insertion from the current bindings.</summary>
    public string BuildHtml()
    {
        var roleLabel = this.source.Role.Value;
        this.renderedRoleLabel = roleLabel;
        return ChatOutputHtmlRenderer.RenderMessage(
            this.ElementId,
            roleLabel,
            this.bindings.Select(binding => (binding.ElementId, binding.Html)).ToList(),
            this.source.Timestamp);
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
        var reasoningChanged = !this.hasRendered || includeReasoning != this.lastReasoningVisible;

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
            this.EmitDiff(newBindings, roleLabel, reasoningChanged);
        }

        this.bindings.Clear();
        this.bindings.AddRange(newBindings);
        this.hasRendered = true;
        this.lastReasoningVisible = includeReasoning;
        this.renderedRoleLabel = roleLabel;
    }

    private void EmitDiff(List<ContentBinding> newBindings, string roleLabel, bool reasoningChanged)
    {
        if (!string.Equals(this.renderedRoleLabel, roleLabel, StringComparison.Ordinal))
        {
            this.sink.UpdateContent(
                ChatOutputHtmlRenderer.HeaderId(this.ElementId),
                ChatOutputUpdateLocation.Replace,
                ChatOutputHtmlRenderer.RenderHeader(this.ElementId, roleLabel, this.source.Timestamp));
        }

        for (var index = 0; index < newBindings.Count; index++)
        {
            if (index < this.bindings.Count)
            {
                if (!reasoningChanged && this.bindings[index].Key == newBindings[index].Key)
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
    private readonly Func<int> nextId;
    private readonly string containerPath;
    private readonly IToolVisualizerFactory? toolFactory;
    private readonly IAgentStatusSink? statusSink;

    public ChatMessageHtmlTransformer(
        IReadOnlyList<AgentChatHistoryItem> source,
        List<RenderSlot> target,
        IChatOutputHtmlSink sink,
        Func<bool> isReasoningVisible,
        Func<int> nextId,
        string containerPath,
        IToolVisualizerFactory? toolFactory = null,
        IAgentStatusSink? statusSink = null)
        : base(source, target)
    {
        this.sink = sink;
        this.isReasoningVisible = isReasoningVisible;
        this.nextId = nextId;
        this.containerPath = containerPath;
        this.toolFactory = toolFactory;
        this.statusSink = statusSink;
        this.ApplyInitialTransform();
    }

    protected override RenderSlot Create(AgentChatHistoryItem sourceItem)
        => new(new ChatMessageHtmlModel(ChatOutputHtmlRenderer.MessageId(this.nextId()), sourceItem, this.isReasoningVisible, this.sink, this.toolFactory, this.statusSink));

    protected override void Update(RenderSlot target, AgentChatHistoryItem sourceItem)
        => target.Model.Update(sourceItem);

    protected override void OnInsert(int index, RenderSlot slot)
    {
        var sourceItem = this.Source[index];

        if (IsToolCallOnlyItem(sourceItem))
        {
            var toolName = GetLastToolName(sourceItem);

            if (index > 0)
            {
                var prevSlot = this.Target[index - 1];

                if (prevSlot.Group is { } existingGroup)
                {
                    // Extend the existing group: no new top-level DOM element needed.
                    existingGroup.AppendItem(slot.Model, toolName);
                    slot.Group = existingGroup;
                    return;
                }

                if (IsToolCallOnlyItem(this.Source[index - 1]))
                {
                    // Previous item was a standalone tool call: promote both into a new group.
                    var groupId = ChatOutputHtmlRenderer.ToolCallGroupId(this.nextId());
                    var prevToolName = GetLastToolName(this.Source[index - 1]);
                    var group = new ToolCallGroupHtmlModel(groupId, this.sink, prevToolName);

                    // Replace the previous standalone message with the group that wraps it.
                    this.sink.UpdateContent(
                        prevSlot.Model.ElementId,
                        ChatOutputUpdateLocation.Replace,
                        group.BuildHtml(prevSlot.Model.BuildHtml()));
                    prevSlot.Group = group;

                    group.AppendItem(slot.Model, toolName);
                    slot.Group = group;
                    return;
                }
            }
        }

        // Standalone insert (non-tool-call, or first/isolated tool call with no adjacent group).
        var (location, reference) = ChatOutputHtmlInsertion.ResolveInsertTarget(
            this.Target,
            index,
            this.containerPath,
            static s => s.Model.IsInserted,
            static s => s.Group?.GroupId ?? s.Model.ElementId);
        this.sink.UpdateContent(reference, location, slot.Model.BuildHtml());
        slot.Model.IsInserted = true;
    }

    protected override void OnRemoveAt(int index, RenderSlot slot)
        => this.sink.RemoveContent(slot.Model.ElementId);

    private static bool IsToolCallOnlyItem(AgentChatHistoryItem item)
    {
        if (item.Contents.Count == 0)
        {
            return false;
        }

        foreach (var content in item.Contents)
        {
            if (content is not (FunctionCallContent or FunctionResultContent))
            {
                return false;
            }
        }

        return true;
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
        IToolVisualizerFactory? toolFactory = null,
        IAgentStatusSink? statusSink = null)
    {
        ArgumentNullException.ThrowIfNull(isReasoningVisible);
        ArgumentNullException.ThrowIfNull(sink);
        this.ElementId = elementId;
        this.Source = source;
        this.isReasoningVisible = isReasoningVisible;
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

    public void Dispose() => this.transformer?.Dispose();
}

/// <summary>Transforms the running-items source collection into <see cref="RunningChatItemHtmlModel"/> instances.</summary>
internal sealed class RunningChatItemsHtmlTransformer : CollectionTransformer<AgentChatRunningItem, RunningChatItemHtmlModel>
{
    private readonly IChatOutputHtmlSink sink;
    private readonly Func<bool> isReasoningVisible;
    private readonly Func<int> nextId;
    private readonly IToolVisualizerFactory? toolFactory;
    private readonly IAgentStatusSink? statusSink;

    public RunningChatItemsHtmlTransformer(
        IReadOnlyList<AgentChatRunningItem> source,
        List<RunningChatItemHtmlModel> target,
        IChatOutputHtmlSink sink,
        Func<bool> isReasoningVisible,
        Func<int> nextId,
        IToolVisualizerFactory? toolFactory = null,
        IAgentStatusSink? statusSink = null)
        : base(source, target)
    {
        this.sink = sink;
        this.isReasoningVisible = isReasoningVisible;
        this.nextId = nextId;
        this.toolFactory = toolFactory;
        this.statusSink = statusSink;
        this.ApplyInitialTransform();
    }

    public IReadOnlyList<RunningChatItemHtmlModel> Models => (List<RunningChatItemHtmlModel>)this.Target;

    protected override RunningChatItemHtmlModel Create(AgentChatRunningItem sourceItem)
        => new(ChatOutputHtmlRenderer.RunningItemId(this.nextId()), sourceItem, this.isReasoningVisible, this.sink, this.nextId, this.toolFactory, this.statusSink);

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
/// </summary>
public sealed class ChatOutputHtmlModel : IDisposable
{
    private readonly IChatOutputHtmlSink sink;
    private readonly IReadOnlyList<AgentChatHistoryItem> historyItems;
    private readonly IReadOnlyList<AgentChatRunningItem> runningItems;
    private readonly ChatMessageHtmlTransformer historyTransformer;
    private readonly RunningChatItemsHtmlTransformer runningTransformer;
    private readonly List<RenderSlot> historySlots = [];
    private readonly List<RunningChatItemHtmlModel> runningModels = [];
    private readonly Dictionary<AgentChatRunningItem, NotifyCollectionChangedEventHandler> runningItemHandlers = [];
    private int idSequence;

    public ChatOutputHtmlModel(
        IReadOnlyList<AgentChatHistoryItem> historyItems,
        IReadOnlyList<AgentChatRunningItem> runningItems,
        Func<bool> isReasoningVisible,
        IChatOutputHtmlSink sink,
        IToolVisualizerFactory? toolFactory = null,
        IAgentStatusSink? statusSink = null)
    {
        ArgumentNullException.ThrowIfNull(historyItems);
        ArgumentNullException.ThrowIfNull(runningItems);
        ArgumentNullException.ThrowIfNull(isReasoningVisible);
        ArgumentNullException.ThrowIfNull(sink);

        this.historyItems = historyItems;
        this.runningItems = runningItems;
        this.sink = sink;

        this.historyTransformer = new ChatMessageHtmlTransformer(
            historyItems,
            this.historySlots,
            sink,
            isReasoningVisible,
            this.NextId,
            ChatOutputHtmlRenderer.HistoryContainerId,
            toolFactory,
            statusSink);
        this.runningTransformer = new RunningChatItemsHtmlTransformer(
            runningItems,
            this.runningModels,
            sink,
            isReasoningVisible,
            this.NextId,
            toolFactory,
            statusSink);

        // Subscribe AFTER the transformers so, for any one collection-changed event, the DOM
        // operations are emitted (by the transformer) before the trailing scroll request.
        if (historyItems is INotifyCollectionChanged historyChanged)
        {
            historyChanged.CollectionChanged += this.OnHistoryCollectionChanged;
        }

        if (runningItems is INotifyCollectionChanged runningChanged)
        {
            runningChanged.CollectionChanged += this.OnRunningCollectionChanged;
        }

        this.SyncRunningItemSubscriptions();
        this.sink.ScrollToBottom();
    }

    /// <summary>Re-renders every message (for example, when reasoning visibility toggles).</summary>
    public void Refresh()
    {
        foreach (var slot in this.historySlots)
        {
            slot.Model.Refresh();
        }

        foreach (var model in this.runningModels)
        {
            model.Refresh();
        }

        this.sink.ScrollToBottom();
    }

    public void Dispose()
    {
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
        this.runningTransformer.Dispose();
        this.historyTransformer.Dispose();
    }

    private int NextId() => this.idSequence++;

    private void OnHistoryCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => this.sink.ScrollToBottom();

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
