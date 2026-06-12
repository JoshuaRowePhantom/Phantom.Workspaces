using System;
using System.Collections.Generic;
using Avalonia.Controls.Documents;
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
    private readonly ChatHistorySelectableInlineModel historyModel;
    private readonly RunningChatItemsSelectableInlineModel runningModel;

    public SelectableTextBlockChatOutputModel(
        IReadOnlyList<AgentChatHistoryItem> historyItems,
        IReadOnlyList<AgentChatRunningItem> runningItems,
        Span historyRootSpan,
        Span runningRootSpan,
        Func<bool> isReasoningVisible)
    {
        this.historyModel = new ChatHistorySelectableInlineModel(
            historyRootSpan,
            historyItems,
            isReasoningVisible);
        this.runningModel = new RunningChatItemsSelectableInlineModel(
            runningRootSpan,
            runningItems,
            isReasoningVisible);
    }

    public void Refresh()
    {
        this.historyModel.Refresh();
        this.runningModel.Refresh();
    }

    public void Dispose()
    {
        this.runningModel.Dispose();
        this.historyModel.Dispose();
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
        this.Span.Inlines.Clear();
        this.Span.Classes.Clear();
        this.Span.Classes.Add("agent-chat-selectable-message");
        this.Span.Classes.Add(string.Equals(this.source.Role.Value, "user", StringComparison.OrdinalIgnoreCase)
            ? "agent-chat-selectable-user-message"
            : "agent-chat-selectable-assistant-message");
        var roleLabel = this.source.Role.Value;

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
                case TextContent textContent:
                    this.AppendLine(textContent.Text);
                    break;
                case FunctionCallContent functionCallContent:
                    this.AppendLine($"tool call: {functionCallContent.Name}", "agent-chat-selectable-meta");
                    this.AppendLine(DocumentBlockUtilities.PrettyJson(functionCallContent.Arguments), "agent-chat-selectable-monospace");
                    break;
                case FunctionResultContent functionResultContent:
                    this.AppendLine($"tool result: {functionResultContent.CallId}", "agent-chat-selectable-meta");
                    this.AppendLine(DocumentBlockUtilities.PrettyJson(functionResultContent.Result), "agent-chat-selectable-monospace");
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
