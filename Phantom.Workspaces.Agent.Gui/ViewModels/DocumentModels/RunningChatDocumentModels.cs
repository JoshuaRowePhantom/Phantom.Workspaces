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
        this.ApplyInitialTransform();
    }

    protected override ChatMessageDocumentModel CreateBlockModel(AgentChatHistoryItem sourceItem)
        => new(sourceItem, isRunning: true, this.isReasoningVisible);

    protected override void UpdateBlockModel(ChatMessageDocumentModel model, AgentChatHistoryItem sourceItem)
        => model.Update(sourceItem);
}

internal sealed class RunningChatItemDocumentModel : AgentChatDocumentBlockModel, IDisposable
{
    private readonly Func<bool> isReasoningVisible;
    private readonly RunningChatItemDocumentModelTransformer transformer;
    private readonly List<ChatMessageDocumentModel> messageModels = [];
    private readonly Section messagesSection = new();

    public RunningChatItemDocumentModel(AgentChatRunningItem runningItem, Func<bool> isReasoningVisible)
    {
        ArgumentNullException.ThrowIfNull(isReasoningVisible);
        this.isReasoningVisible = isReasoningVisible;
        this.Source = runningItem;
        this.Section = new Section();
        this.Section.Blocks.Add(this.messagesSection);
        this.transformer = new RunningChatItemDocumentModelTransformer(this.messagesSection, runningItem.Items, this.isReasoningVisible, this.messageModels);
    }

    public AgentChatRunningItem Source { get; private set; }

    public Section Section { get; }

    public override Block Block => this.Section;

    public void Update(AgentChatRunningItem runningItem)
    {
        this.Source = runningItem;
    }

    public void Refresh()
    {
        for (var index = 0; index < this.messageModels.Count; index++)
        {
            this.messageModels[index].Update(this.messageModels[index].Source);
        }
    }

    public void Dispose() => this.transformer.Dispose();
}
