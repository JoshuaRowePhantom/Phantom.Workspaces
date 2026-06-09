using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;

internal sealed class RunningChatItemsDocumentModelTransformer : AgentChatDocumentBlockModelCollectionTransformer<AgentChatRunningItem, RunningChatItemDocumentModel>
{
    private readonly Func<bool> isReasoningVisible;

    public RunningChatItemsDocumentModelTransformer(
        Section rootSection,
        IReadOnlyList<AgentChatRunningItem> runningItems,
        Func<bool> isReasoningVisible,
        List<RunningChatItemDocumentModel> runningItemModels)
        : base(
            runningItems,
            runningItemModels,
            rootSection.Blocks)
    {
        ArgumentNullException.ThrowIfNull(rootSection);
        ArgumentNullException.ThrowIfNull(isReasoningVisible);
        this.isReasoningVisible = isReasoningVisible;
        this.ApplyInitialTransform();
    }

    protected override RunningChatItemDocumentModel CreateBlockModel(AgentChatRunningItem sourceItem)
        => new(sourceItem, this.isReasoningVisible);

    protected override void UpdateBlockModel(RunningChatItemDocumentModel model, AgentChatRunningItem sourceItem)
        => model.Update(sourceItem);
}

internal sealed class RunningChatItemsDocumentModel : IDisposable
{
    private readonly RunningChatItemsDocumentModelTransformer transformer;
    private readonly List<RunningChatItemDocumentModel> runningItemModels = [];

    public RunningChatItemsDocumentModel(
        Section rootSection,
        IReadOnlyList<AgentChatRunningItem> runningItems,
        Func<bool> isReasoningVisible)
    {
        this.transformer = new RunningChatItemsDocumentModelTransformer(rootSection, runningItems, isReasoningVisible, this.runningItemModels);
    }

    public void Dispose() => this.transformer.Dispose();

    public void Refresh()
    {
        for (var index = 0; index < this.runningItemModels.Count; index++)
        {
            this.runningItemModels[index].Refresh();
        }
    }
}

internal sealed class RunningChatItemDocumentModelTransformer : AgentChatDocumentBlockModelCollectionTransformer<AgentChatHistoryItem, ChatMessageDocumentModel>
{
    private readonly Func<bool> isReasoningVisible;

    public RunningChatItemDocumentModelTransformer(
        Section messagesSection,
        IReadOnlyList<AgentChatHistoryItem> historyItems,
        Func<bool> isReasoningVisible,
        List<ChatMessageDocumentModel> messageModels)
        : base(
            historyItems,
            messageModels,
            messagesSection.Blocks)
    {
        ArgumentNullException.ThrowIfNull(messagesSection);
        ArgumentNullException.ThrowIfNull(isReasoningVisible);
        this.isReasoningVisible = isReasoningVisible;
        this.HistoryItems = historyItems;
        this.ApplyInitialTransform();
    }

    public IReadOnlyList<AgentChatHistoryItem> HistoryItems { get; }

    protected override ChatMessageDocumentModel CreateBlockModel(AgentChatHistoryItem sourceItem)
        => new(sourceItem, this.isReasoningVisible);

    protected override void UpdateBlockModel(ChatMessageDocumentModel model, AgentChatHistoryItem sourceItem)
        => model.Update(sourceItem);
}

internal sealed class RunningChatItemDocumentModel : AgentChatDocumentBlockModel, IDisposable
{
    private readonly Func<bool> isReasoningVisible;
    private RunningChatItemDocumentModelTransformer transformer;
    private readonly List<ChatMessageDocumentModel> messageModels = [];
    private readonly Section messagesSection = new();
    private readonly Section progressSection = new();

    public RunningChatItemDocumentModel(AgentChatRunningItem runningItem, Func<bool> isReasoningVisible)
    {
        ArgumentNullException.ThrowIfNull(isReasoningVisible);
        this.isReasoningVisible = isReasoningVisible;
        this.Source = runningItem;
        this.Section = new Section();
        this.Section.Blocks.Add(this.messagesSection);
        this.Section.Blocks.Add(this.progressSection);
        
        // Add progress bar for the running item
        var progressBar = new ProgressBar
        {
            IsIndeterminate = true,
        };
        progressBar.Classes.Add("agent-chat-running-progress");
        this.progressSection.Blocks.Add(new BlockUIContainer(progressBar));
        
        this.transformer = new RunningChatItemDocumentModelTransformer(this.messagesSection, runningItem.Items, this.isReasoningVisible, this.messageModels);
    }

    public AgentChatRunningItem Source { get; private set; }

    public Section Section { get; }

    public override Block Block => this.Section;

    public void Update(AgentChatRunningItem runningItem)
    {
        this.Source = runningItem;
        
        // If it's a different running item, recreate the transformer to watch the new Items collection
        if (!ReferenceEquals(this.transformer.HistoryItems, runningItem.Items))
        {
            this.transformer.Dispose();
            this.messageModels.Clear();
            this.messagesSection.Blocks.Clear();
            this.transformer = new RunningChatItemDocumentModelTransformer(this.messagesSection, runningItem.Items, this.isReasoningVisible, this.messageModels);
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
