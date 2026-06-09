using Avalonia.Controls.Documents;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;

internal sealed class ChatHistoryDocumentModelTransformer : AgentChatDocumentBlockModelCollectionTransformer<AgentChatHistoryItem, ChatMessageDocumentModel>
{
    private readonly Func<bool> isReasoningVisible;

    public ChatHistoryDocumentModelTransformer(
        Section rootSection,
        IReadOnlyList<AgentChatHistoryItem> historyItems,
        Func<bool> isReasoningVisible,
        List<ChatMessageDocumentModel> messageModels)
        : base(
            historyItems,
            messageModels,
            rootSection.Blocks)
    {
        ArgumentNullException.ThrowIfNull(rootSection);
        ArgumentNullException.ThrowIfNull(isReasoningVisible);
        this.isReasoningVisible = isReasoningVisible;
        this.ApplyInitialTransform();
    }

    protected override ChatMessageDocumentModel CreateBlockModel(AgentChatHistoryItem sourceItem)
        => new(sourceItem, this.isReasoningVisible);

    protected override void UpdateBlockModel(ChatMessageDocumentModel model, AgentChatHistoryItem sourceItem)
        => model.Update(sourceItem);
}

internal sealed class ChatHistoryDocumentModel : IDisposable
{
    private readonly ChatHistoryDocumentModelTransformer transformer;
    private readonly List<ChatMessageDocumentModel> messageModels = [];

    public ChatHistoryDocumentModel(
        Section rootSection,
        IReadOnlyList<AgentChatHistoryItem> historyItems,
        Func<bool> isReasoningVisible)
    {
        this.transformer = new ChatHistoryDocumentModelTransformer(rootSection, historyItems, isReasoningVisible, this.messageModels);
    }

    public void Dispose() => this.transformer.Dispose();

    public void Refresh()
    {
        for (var index = 0; index < this.messageModels.Count; index++)
        {
            this.messageModels[index].Refresh();
        }
    }
}
