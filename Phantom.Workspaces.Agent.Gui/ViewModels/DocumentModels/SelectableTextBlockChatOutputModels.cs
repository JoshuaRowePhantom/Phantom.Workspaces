using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels.Collections;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;

internal abstract class AgentChatSelectableInlineModel
{
    public abstract Inline Inline { get; }
}

internal abstract class AgentChatSelectableInlineCollectionTransformer<TSource, TTarget> : CollectionTransformer<TSource, TTarget>
    where TTarget : AgentChatSelectableInlineModel
{
    private readonly InlineCollection inlines;

    protected AgentChatSelectableInlineCollectionTransformer(
        IReadOnlyList<TSource> source,
        IList<TTarget> target,
        InlineCollection inlines)
        : base(source, target)
    {
        ArgumentNullException.ThrowIfNull(inlines);
        this.inlines = inlines;
    }

    protected abstract TTarget CreateInlineModel(TSource sourceItem);

    protected abstract void UpdateInlineModel(TTarget targetItem, TSource sourceItem);

    protected sealed override TTarget Create(TSource sourceItem) => this.CreateInlineModel(sourceItem);

    protected sealed override void Update(TTarget targetItem, TSource sourceItem)
        => this.UpdateInlineModel(targetItem, sourceItem);

    protected override void OnInsert(int index, TTarget targetItem)
        => this.inlines.Insert(index, targetItem.Inline);

    protected override void OnRemoveAt(int index, TTarget targetItem)
        => this.inlines.RemoveAt(index);

    protected override void OnRemoved(TTarget targetItem)
    {
        if (targetItem is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

internal sealed class SelectableTextBlockChatOutputModel : IDisposable
{
    private readonly IReadOnlyList<AgentChatHistoryItem> historyItems;
    private readonly IReadOnlyList<AgentChatRunningItem> runningItems;
    private readonly ChatHistorySelectableInlineModel historyModel;
    private readonly RunningChatItemsSelectableInlineModel runningModel;
    private readonly Dictionary<AgentChatRunningItem, NotifyCollectionChangedEventHandler> runningItemHandlers = [];

    public SelectableTextBlockChatOutputModel(
        IReadOnlyList<AgentChatHistoryItem> historyItems,
        IReadOnlyList<AgentChatRunningItem> runningItems,
        Span historyRootSpan,
        Span runningRootSpan,
        Func<bool> isReasoningVisible)
    {
        this.historyItems = historyItems;
        this.runningItems = runningItems;
        this.historyModel = new ChatHistorySelectableInlineModel(
            historyRootSpan,
            historyItems,
            isReasoningVisible);
        this.runningModel = new RunningChatItemsSelectableInlineModel(
            runningRootSpan,
            runningItems,
            isReasoningVisible);

        // Raise a single ContentChanged signal from the view model so the view can react
        // (for example, to keep scrolled to the bottom) without walking the inline tree.
        if (historyItems is INotifyCollectionChanged historyChanged)
        {
            historyChanged.CollectionChanged += this.OnContentCollectionChanged;
        }

        if (runningItems is INotifyCollectionChanged runningChanged)
        {
            runningChanged.CollectionChanged += this.OnRunningCollectionChanged;
        }

        this.SyncRunningItemSubscriptions();
    }

    /// <summary>Raised whenever the rendered selectable output content changes.</summary>
    public event EventHandler? ContentChanged;

    public void Refresh()
    {
        this.historyModel.Refresh();
        this.runningModel.Refresh();
        this.RaiseContentChanged();
    }

    public void Dispose()
    {
        if (this.historyItems is INotifyCollectionChanged historyChanged)
        {
            historyChanged.CollectionChanged -= this.OnContentCollectionChanged;
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

        this.runningModel.Dispose();
        this.historyModel.Dispose();
    }

    private void OnContentCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => this.RaiseContentChanged();

    private void OnRunningCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        this.SyncRunningItemSubscriptions();
        this.RaiseContentChanged();
    }

    private void OnRunningItemMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => this.RaiseContentChanged();

    private void RaiseContentChanged() => this.ContentChanged?.Invoke(this, EventArgs.Empty);

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

internal sealed class ChatHistorySelectableInlineModelTransformer : AgentChatSelectableInlineCollectionTransformer<AgentChatHistoryItem, ChatMessageSelectableInlineModel>
{
    private readonly Func<bool> isReasoningVisible;

    public ChatHistorySelectableInlineModelTransformer(
        Span rootSpan,
        IReadOnlyList<AgentChatHistoryItem> historyItems,
        Func<bool> isReasoningVisible,
        List<ChatMessageSelectableInlineModel> messageModels)
        : base(
            historyItems,
            messageModels,
            rootSpan.Inlines)
    {
        ArgumentNullException.ThrowIfNull(rootSpan);
        ArgumentNullException.ThrowIfNull(isReasoningVisible);
        this.isReasoningVisible = isReasoningVisible;
        this.ApplyInitialTransform();
    }

    protected override ChatMessageSelectableInlineModel CreateInlineModel(AgentChatHistoryItem sourceItem)
        => new(sourceItem, this.isReasoningVisible);

    protected override void UpdateInlineModel(ChatMessageSelectableInlineModel targetItem, AgentChatHistoryItem sourceItem)
        => targetItem.Update(sourceItem);
}

internal sealed class ChatHistorySelectableInlineModel : IDisposable
{
    private readonly ChatHistorySelectableInlineModelTransformer transformer;
    private readonly List<ChatMessageSelectableInlineModel> messageModels = [];

    public ChatHistorySelectableInlineModel(
        Span rootSpan,
        IReadOnlyList<AgentChatHistoryItem> historyItems,
        Func<bool> isReasoningVisible)
    {
        this.transformer = new ChatHistorySelectableInlineModelTransformer(rootSpan, historyItems, isReasoningVisible, this.messageModels);
    }

    public void Refresh()
    {
        for (var index = 0; index < this.messageModels.Count; index++)
        {
            this.messageModels[index].Refresh();
        }
    }

    public void Dispose() => this.transformer.Dispose();
}

internal sealed class RunningChatItemsSelectableInlineModelTransformer : AgentChatSelectableInlineCollectionTransformer<AgentChatRunningItem, RunningChatItemSelectableInlineModel>
{
    private readonly Func<bool> isReasoningVisible;

    public RunningChatItemsSelectableInlineModelTransformer(
        Span rootSpan,
        IReadOnlyList<AgentChatRunningItem> runningItems,
        Func<bool> isReasoningVisible,
        List<RunningChatItemSelectableInlineModel> runningItemModels)
        : base(
            runningItems,
            runningItemModels,
            rootSpan.Inlines)
    {
        ArgumentNullException.ThrowIfNull(rootSpan);
        ArgumentNullException.ThrowIfNull(isReasoningVisible);
        this.isReasoningVisible = isReasoningVisible;
        this.ApplyInitialTransform();
    }

    protected override RunningChatItemSelectableInlineModel CreateInlineModel(AgentChatRunningItem sourceItem)
        => new(sourceItem, this.isReasoningVisible);

    protected override void UpdateInlineModel(RunningChatItemSelectableInlineModel targetItem, AgentChatRunningItem sourceItem)
        => targetItem.Update(sourceItem);
}

internal sealed class RunningChatItemsSelectableInlineModel : IDisposable
{
    private readonly RunningChatItemsSelectableInlineModelTransformer transformer;
    private readonly List<RunningChatItemSelectableInlineModel> runningItemModels = [];

    public RunningChatItemsSelectableInlineModel(
        Span rootSpan,
        IReadOnlyList<AgentChatRunningItem> runningItems,
        Func<bool> isReasoningVisible)
    {
        this.transformer = new RunningChatItemsSelectableInlineModelTransformer(rootSpan, runningItems, isReasoningVisible, this.runningItemModels);
    }

    public void Refresh()
    {
        for (var index = 0; index < this.runningItemModels.Count; index++)
        {
            this.runningItemModels[index].Refresh();
        }
    }

    public void Dispose() => this.transformer.Dispose();
}

internal sealed class RunningChatItemSelectableInlineModelTransformer : AgentChatSelectableInlineCollectionTransformer<AgentChatHistoryItem, ChatMessageSelectableInlineModel>
{
    private readonly Func<bool> isReasoningVisible;

    public RunningChatItemSelectableInlineModelTransformer(
        Span messagesSpan,
        IReadOnlyList<AgentChatHistoryItem> historyItems,
        Func<bool> isReasoningVisible,
        List<ChatMessageSelectableInlineModel> messageModels)
        : base(
            historyItems,
            messageModels,
            messagesSpan.Inlines)
    {
        ArgumentNullException.ThrowIfNull(messagesSpan);
        ArgumentNullException.ThrowIfNull(isReasoningVisible);
        this.isReasoningVisible = isReasoningVisible;
        this.HistoryItems = historyItems;
        this.ApplyInitialTransform();
    }

    public IReadOnlyList<AgentChatHistoryItem> HistoryItems { get; }

    protected override ChatMessageSelectableInlineModel CreateInlineModel(AgentChatHistoryItem sourceItem)
        => new(sourceItem, this.isReasoningVisible);

    protected override void UpdateInlineModel(ChatMessageSelectableInlineModel targetItem, AgentChatHistoryItem sourceItem)
        => targetItem.Update(sourceItem);
}

internal sealed class RunningChatItemSelectableInlineModel : AgentChatSelectableInlineModel, IDisposable
{
    private readonly Func<bool> isReasoningVisible;
    private readonly List<ChatMessageSelectableInlineModel> messageModels = [];
    private readonly Span messagesSpan = new();
    private RunningChatItemSelectableInlineModelTransformer transformer;

    public RunningChatItemSelectableInlineModel(AgentChatRunningItem runningItem, Func<bool> isReasoningVisible)
    {
        ArgumentNullException.ThrowIfNull(isReasoningVisible);
        this.isReasoningVisible = isReasoningVisible;
        this.Source = runningItem;
        this.Span = new Span();
        this.Span.Inlines.Add(this.messagesSpan);
        this.transformer = new RunningChatItemSelectableInlineModelTransformer(
            this.messagesSpan,
            runningItem.Items,
            this.isReasoningVisible,
            this.messageModels);
    }

    public AgentChatRunningItem Source { get; private set; }

    public Span Span { get; }

    public override Inline Inline => this.Span;

    public void Update(AgentChatRunningItem runningItem)
    {
        this.Source = runningItem;

        if (!ReferenceEquals(this.transformer.HistoryItems, runningItem.Items))
        {
            this.transformer.Dispose();
            this.messageModels.Clear();
            this.messagesSpan.Inlines.Clear();
            this.transformer = new RunningChatItemSelectableInlineModelTransformer(
                this.messagesSpan,
                runningItem.Items,
                this.isReasoningVisible,
                this.messageModels);
        }
    }

    public void Refresh()
    {
        for (var index = 0; index < this.messageModels.Count; index++)
        {
            this.messageModels[index].Refresh();
        }
    }

    public void Dispose() => this.transformer.Dispose();
}

internal sealed class ChatMessageSelectableInlineModel : AgentChatSelectableInlineModel
{
    private readonly Func<bool> isReasoningVisible;
    private readonly Dictionary<string, bool> toolExpansionState = new(StringComparer.Ordinal);
    private readonly List<ToolContentSelectableInlineModel> toolModels = [];
    private AgentChatHistoryItem source;

    public ChatMessageSelectableInlineModel(
        AgentChatHistoryItem source,
        Func<bool> isReasoningVisible)
    {
        ArgumentNullException.ThrowIfNull(isReasoningVisible);
        this.source = source;
        this.isReasoningVisible = isReasoningVisible;
        this.Span = new Span();
        this.Render();
    }

    public Span Span { get; }

    public override Inline Inline => this.Span;

    public void Update(AgentChatHistoryItem source)
    {
        this.source = source;
        this.Render();
    }

    public void Refresh() => this.Render();

    private void Render()
    {
        this.ClearToolModels();
        this.Span.Inlines.Clear();
        this.Span.Classes.Clear();
        this.Span.Classes.Add("agent-chat-selectable-message");
        this.Span.Classes.Add(string.Equals(this.source.Role.Value, "user", StringComparison.OrdinalIgnoreCase)
            ? "agent-chat-selectable-user-message"
            : "agent-chat-selectable-assistant-message");
        var roleLabel = this.source.Role.Value;
        var isDiagnostic = string.Equals(
            roleLabel,
            AgentChatHistoryItem.DiagnosticChatRole.Value,
            StringComparison.OrdinalIgnoreCase);

        this.AppendLine($"[{roleLabel}]", "agent-chat-selectable-role-label");

        for (var index = 0; index < this.source.Contents.Count; index++)
        {
            var contentItem = this.source.Contents[index];
            if (!this.isReasoningVisible() && contentItem is TextReasoningContent)
            {
                continue;
            }

            switch (contentItem)
            {
                case TextReasoningContent reasoningContent:
                    this.AppendLine(reasoningContent.Text, "agent-chat-selectable-reasoning");
                    break;
                case TextContent textContent when isDiagnostic && !string.IsNullOrWhiteSpace(textContent.Text):
                    this.AppendDiagnosticContent(index, textContent.Text);
                    break;
                case TextContent textContent:
                    this.AppendLine(textContent.Text);
                    break;
                case FunctionCallContent functionCallContent:
                    this.AppendToolContent(
                        $"call:{functionCallContent.CallId}",
                        $"tool call: {functionCallContent.Name}",
                        () => DocumentBlockUtilities.PrettyJson(functionCallContent.Arguments));
                    break;
                case FunctionResultContent functionResultContent:
                    this.AppendToolContent(
                        $"result:{functionResultContent.CallId}",
                        $"tool result: {functionResultContent.CallId}",
                        () => DocumentBlockUtilities.PrettyJson(functionResultContent.Result));
                    break;
                case DataContent dataContent:
                    if (DocumentBlockUtilities.IsImageMediaType(dataContent.MediaType))
                    {
                        var imageLabel = string.IsNullOrWhiteSpace(dataContent.MediaType) ? "image" : dataContent.MediaType;
                        this.AppendLine(imageLabel, "agent-chat-selectable-meta");
                    }
                    else
                    {
                        var mediaLabel = string.IsNullOrWhiteSpace(dataContent.MediaType) ? "[data]" : $"[{dataContent.MediaType}]";
                        this.AppendLine(mediaLabel, "agent-chat-selectable-monospace");
                    }
                    break;
                case ErrorContent errorContent:
                    this.AppendLine(errorContent.Message, "agent-chat-selectable-error");
                    break;
                case UriContent uriContent:
                    this.AppendLine(uriContent.Uri.ToString(), "agent-chat-selectable-uri");
                    break;
                default:
                    this.AppendLine(contentItem.ToString() ?? string.Empty);
                    break;
            }
        }

        this.Span.Inlines.Add(new LineBreak());
    }

    private void AppendToolContent(string stateKey, string headerLabel, Func<string> dataTextFactory)
    {
        var initiallyExpanded = this.toolExpansionState.TryGetValue(stateKey, out var expanded) && expanded;
        var toolModel = new ToolContentSelectableInlineModel(
            headerLabel,
            dataTextFactory,
            initiallyExpanded,
            isExpanded => this.toolExpansionState[stateKey] = isExpanded);
        this.toolModels.Add(toolModel);
        this.Span.Inlines.Add(toolModel.Inline);
    }

    private void AppendDiagnosticContent(int index, string text)
    {
        // Render diagnostic text as a collapsible region (collapsed by default) like tool content:
        // the first line is the always-visible header and the remainder is revealed when expanded.
        var trimmed = text.TrimEnd();
        var newlineIndex = trimmed.IndexOf('\n');
        string header;
        string body;
        if (newlineIndex >= 0)
        {
            header = trimmed[..newlineIndex].TrimEnd('\r');
            body = trimmed[(newlineIndex + 1)..];
        }
        else
        {
            header = trimmed;
            body = string.Empty;
        }

        var stateKey = $"diagnostic:{index}";
        var initiallyExpanded = this.toolExpansionState.TryGetValue(stateKey, out var expanded) && expanded;
        var diagnosticModel = new ToolContentSelectableInlineModel(
            header,
            () => body,
            initiallyExpanded,
            isExpanded => this.toolExpansionState[stateKey] = isExpanded,
            dataClassName: null);
        this.toolModels.Add(diagnosticModel);
        this.Span.Inlines.Add(diagnosticModel.Inline);
    }

    private void ClearToolModels()
    {
        for (var index = 0; index < this.toolModels.Count; index++)
        {
            this.toolModels[index].Dispose();
        }

        this.toolModels.Clear();
    }

    private void AppendLine(string text, string? className = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var lineSpan = new Span();
        var run = new Run(text);

        if (!string.IsNullOrWhiteSpace(className))
        {
            lineSpan.Classes.Add(className);
            run.Classes.Add(className);
        }

        lineSpan.Inlines.Add(run);
        lineSpan.Inlines.Add(new LineBreak());
        this.Span.Inlines.Add(lineSpan);
    }
}

/// <summary>
/// Renders a single tool call or tool result as a collapsible expander. The tool data
/// span starts empty (collapsed) and is populated only when the expander is expanded,
/// and cleared again when it is collapsed, so collapsed tool content never participates
/// in rendering during streaming. Tool results that parse as JSON are pretty-printed.
/// </summary>
internal sealed class ToolContentSelectableInlineModel : IDisposable
{
    private const string CollapsedIndicator = "\u25B8";
    private const string ExpandedIndicator = "\u25BE";

    private readonly Func<string> dataTextFactory;
    private readonly Action<bool>? expandedChanged;
    private readonly string headerLabel;
    private readonly string? dataClassName;
    private readonly Span dataSpan = new();
    private readonly ToggleButton toggleButton;

    private bool isExpanded;
    private bool isUpdatingToggle;
    private string? cachedDataText;

    public ToolContentSelectableInlineModel(
        string headerLabel,
        Func<string> dataTextFactory,
        bool initiallyExpanded,
        Action<bool>? expandedChanged = null,
        string? dataClassName = "agent-chat-selectable-monospace")
    {
        ArgumentNullException.ThrowIfNull(headerLabel);
        ArgumentNullException.ThrowIfNull(dataTextFactory);
        this.headerLabel = headerLabel;
        this.dataTextFactory = dataTextFactory;
        this.expandedChanged = expandedChanged;
        this.dataClassName = dataClassName;
        this.isExpanded = initiallyExpanded;

        this.dataSpan.Classes.Add("agent-chat-selectable-tool-data");
        if (!string.IsNullOrWhiteSpace(dataClassName))
        {
            this.dataSpan.Classes.Add(dataClassName);
        }

        this.toggleButton = new ToggleButton
        {
            IsChecked = initiallyExpanded,
        };
        this.toggleButton.Classes.Add("agent-chat-selectable-tool-toggle");
        this.toggleButton.IsCheckedChanged += this.OnToggleCheckedChanged;
        this.UpdateToggleContent();

        this.Span = new Span();
        this.Span.Classes.Add("agent-chat-selectable-tool");
        this.Span.Inlines.Add(new InlineUIContainer(this.toggleButton));
        this.Span.Inlines.Add(this.dataSpan);

        this.ApplyExpansion();
    }

    public Span Span { get; }

    public Inline Inline => this.Span;

    public bool IsExpanded => this.isExpanded;

    // Exposed for tests to observe lazily-populated tool data.
    public Span DataSpan => this.dataSpan;

    public void SetExpanded(bool expanded)
    {
        if (this.isExpanded == expanded)
        {
            return;
        }

        this.isExpanded = expanded;
        this.UpdateToggleContent();
        this.expandedChanged?.Invoke(expanded);
        this.ApplyExpansion();
    }

    private void OnToggleCheckedChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (this.isUpdatingToggle)
        {
            return;
        }

        this.SetExpanded(this.toggleButton.IsChecked == true);
    }

    private void UpdateToggleContent()
    {
        this.isUpdatingToggle = true;
        try
        {
            if (this.toggleButton.IsChecked != this.isExpanded)
            {
                this.toggleButton.IsChecked = this.isExpanded;
            }

            var indicator = this.isExpanded ? ExpandedIndicator : CollapsedIndicator;
            this.toggleButton.Content = $"{indicator} {this.headerLabel}";
        }
        finally
        {
            this.isUpdatingToggle = false;
        }
    }

    private void ApplyExpansion()
    {
        this.dataSpan.Inlines.Clear();

        if (!this.isExpanded)
        {
            return;
        }

        this.cachedDataText ??= this.dataTextFactory() ?? string.Empty;
        if (string.IsNullOrEmpty(this.cachedDataText))
        {
            return;
        }

        var run = new Run(this.cachedDataText);
        if (!string.IsNullOrWhiteSpace(this.dataClassName))
        {
            run.Classes.Add(this.dataClassName);
        }

        this.dataSpan.Inlines.Add(run);
        this.dataSpan.Inlines.Add(new LineBreak());
    }

    public void Dispose()
    {
        this.toggleButton.IsCheckedChanged -= this.OnToggleCheckedChanged;
    }
}
