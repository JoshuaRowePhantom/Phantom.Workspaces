using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels.Collections;
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
    private readonly List<ContentBinding> bindings = [];
    private AgentChatHistoryItem source;
    private string? renderedRoleLabel;
    private bool hasRendered;
    private bool lastReasoningVisible;

    public ChatMessageHtmlModel(
        string elementId,
        AgentChatHistoryItem source,
        Func<bool> isReasoningVisible,
        IChatOutputHtmlSink sink)
    {
        ArgumentNullException.ThrowIfNull(isReasoningVisible);
        ArgumentNullException.ThrowIfNull(sink);
        this.ElementId = elementId;
        this.source = source;
        this.isReasoningVisible = isReasoningVisible;
        this.sink = sink;
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
            this.bindings.Select(binding => (binding.ElementId, binding.Html)).ToList());
    }

    public void Update(AgentChatHistoryItem newSource)
    {
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

        var newBindings = new List<ContentBinding>(this.source.Contents.Count);
        foreach (var content in this.source.Contents)
        {
            var elementId = ChatOutputHtmlRenderer.ContentId(this.ElementId, newBindings.Count);
            var html = ChatOutputHtmlRenderer.RenderContent(elementId, content, includeReasoning, isDiagnostic);
            if (html is null)
            {
                continue;
            }

            var key = ChatOutputHtmlRenderer.ComputeContentKey(content, isDiagnostic);
            newBindings.Add(new ContentBinding(key, elementId, html));
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
                ChatOutputHtmlRenderer.RenderHeader(this.ElementId, roleLabel));
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
/// Transforms a chat-message source collection into <see cref="ChatMessageHtmlModel"/> instances,
/// emitting insertion/removal operations against a parent container in the DOM.
/// </summary>
internal sealed class ChatMessageHtmlTransformer : CollectionTransformer<AgentChatHistoryItem, ChatMessageHtmlModel>
{
    private readonly IChatOutputHtmlSink sink;
    private readonly Func<bool> isReasoningVisible;
    private readonly Func<int> nextId;
    private readonly string containerPath;

    public ChatMessageHtmlTransformer(
        IReadOnlyList<AgentChatHistoryItem> source,
        List<ChatMessageHtmlModel> target,
        IChatOutputHtmlSink sink,
        Func<bool> isReasoningVisible,
        Func<int> nextId,
        string containerPath)
        : base(source, target)
    {
        this.sink = sink;
        this.isReasoningVisible = isReasoningVisible;
        this.nextId = nextId;
        this.containerPath = containerPath;
        this.ApplyInitialTransform();
    }

    public IReadOnlyList<ChatMessageHtmlModel> Models => (List<ChatMessageHtmlModel>)this.Target;

    protected override ChatMessageHtmlModel Create(AgentChatHistoryItem sourceItem)
        => new(ChatOutputHtmlRenderer.MessageId(this.nextId()), sourceItem, this.isReasoningVisible, this.sink);

    protected override void Update(ChatMessageHtmlModel target, AgentChatHistoryItem sourceItem)
        => target.Update(sourceItem);

    protected override void OnInsert(int index, ChatMessageHtmlModel target)
    {
        var (location, reference) = ChatOutputHtmlInsertion.ResolveInsertTarget(
            this.Target,
            index,
            this.containerPath,
            static model => model.IsInserted,
            static model => model.ElementId);
        this.sink.UpdateContent(reference, location, target.BuildHtml());
        target.IsInserted = true;
    }

    protected override void OnRemoveAt(int index, ChatMessageHtmlModel target)
        => this.sink.RemoveContent(target.ElementId);
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
    private readonly List<ChatMessageHtmlModel> messageModels = [];
    private ChatMessageHtmlTransformer? transformer;

    public RunningChatItemHtmlModel(
        string elementId,
        AgentChatRunningItem source,
        Func<bool> isReasoningVisible,
        IChatOutputHtmlSink sink,
        Func<int> nextId)
    {
        ArgumentNullException.ThrowIfNull(isReasoningVisible);
        ArgumentNullException.ThrowIfNull(sink);
        this.ElementId = elementId;
        this.Source = source;
        this.isReasoningVisible = isReasoningVisible;
        this.sink = sink;
        this.nextId = nextId;
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
            this.messageModels,
            this.sink,
            this.isReasoningVisible,
            this.nextId,
            ChatOutputHtmlRenderer.RunningItemContentsId(this.ElementId));
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
        this.messageModels.Clear();
        this.sink.UpdateContent(
            ChatOutputHtmlRenderer.RunningItemContentsId(this.ElementId),
            ChatOutputUpdateLocation.Replace,
            $"<div class=\"chat-running-contents\" id=\"{ChatOutputHtmlRenderer.RunningItemContentsId(this.ElementId)}\"></div>");
        this.Activate();
    }

    public void Refresh()
    {
        foreach (var model in this.messageModels)
        {
            model.Refresh();
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

    public RunningChatItemsHtmlTransformer(
        IReadOnlyList<AgentChatRunningItem> source,
        List<RunningChatItemHtmlModel> target,
        IChatOutputHtmlSink sink,
        Func<bool> isReasoningVisible,
        Func<int> nextId)
        : base(source, target)
    {
        this.sink = sink;
        this.isReasoningVisible = isReasoningVisible;
        this.nextId = nextId;
        this.ApplyInitialTransform();
    }

    public IReadOnlyList<RunningChatItemHtmlModel> Models => (List<RunningChatItemHtmlModel>)this.Target;

    protected override RunningChatItemHtmlModel Create(AgentChatRunningItem sourceItem)
        => new(ChatOutputHtmlRenderer.RunningItemId(this.nextId()), sourceItem, this.isReasoningVisible, this.sink, this.nextId);

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
    private readonly List<ChatMessageHtmlModel> historyModels = [];
    private readonly List<RunningChatItemHtmlModel> runningModels = [];
    private readonly Dictionary<AgentChatRunningItem, NotifyCollectionChangedEventHandler> runningItemHandlers = [];
    private int idSequence;

    public ChatOutputHtmlModel(
        IReadOnlyList<AgentChatHistoryItem> historyItems,
        IReadOnlyList<AgentChatRunningItem> runningItems,
        Func<bool> isReasoningVisible,
        IChatOutputHtmlSink sink)
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
            this.historyModels,
            sink,
            isReasoningVisible,
            this.NextId,
            ChatOutputHtmlRenderer.HistoryContainerId);
        this.runningTransformer = new RunningChatItemsHtmlTransformer(
            runningItems,
            this.runningModels,
            sink,
            isReasoningVisible,
            this.NextId);

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
        foreach (var model in this.historyModels)
        {
            model.Refresh();
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
