using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Threading;
using Avalonia.Controls.Documents;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class AgentViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly AgentChat agentChat;
    private readonly AgentChatConversationDetailViewModel conversationDetail;
    private readonly AgentChatDetailsViewModel chatDetailsDetail;
    private readonly AgentChatToolsDetailViewModel toolsDetail;
    private readonly AgentChatPlaceholderDetailViewModel backgroundTasksDetail;
    private readonly AgentChatPlaceholderDetailViewModel subAgentsDetail;
    private Section outputHistoryRootSection = new();
    private Section outputRunningRootSection = new();
    private ChatHistoryDocumentModel? historyDocumentModel;
    private RunningChatItemsDocumentModel? runningDocumentModel;
    private bool isReasoningVisible;
    private string agentSessionId;
    private AgentEditorNavigationItemViewModel? selectedEditorItem;

    public AgentViewModel(AgentChat agentChat, string displayName)
    {
        this.agentChat = agentChat;
        this.agentSessionId = agentChat.AgentSessionId;
        this.DisplayName = displayName;
        this.conversationDetail = new AgentChatConversationDetailViewModel(this);
        this.chatDetailsDetail = new AgentChatDetailsViewModel(this);
        this.toolsDetail = new AgentChatToolsDetailViewModel();
        this.backgroundTasksDetail = new AgentChatPlaceholderDetailViewModel(
            "Background tasks",
            "Background task model coming later.");
        this.subAgentsDetail = new AgentChatPlaceholderDetailViewModel(
            "Sub-agents",
            "Sub-agent model coming later.");
        this.InterruptCommand = new RelayCommand(agentChat.Interrupt);
        this.InputQueue = new InputQueueViewModel(
            this.agentChat,
            this.agentChat.DefaultInputQueue,
            this.agentChat.InputQueueManager);
        this.EditorItems = [];
        this.OutputDocument = AgentChatFlowDocumentBuilder.CreateDocument();
        this.AttachOutputDocumentModels();

        this.agentChat.AgentSessionIdChanged += this.OnAgentSessionIdChanged;
        agentChat.ToolsChanged += this.OnToolsChanged;
        this.ApplyToolSnapshot(agentChat.GetToolSnapshot());
    }

    public string DisplayName { get; }

    public string AgentSessionId
    {
        get => this.agentSessionId;
        private set
        {
            if (this.SetProperty(ref this.agentSessionId, value))
            {
                this.chatDetailsDetail.UpdateSessionId(value);
            }
        }
    }

    public AgentChat AgentChat => this.agentChat;

    public ICommand InterruptCommand { get; }

    public InputQueueViewModel InputQueue { get; }

    public ReadOnlyObservableCollection<AgentChatHistoryItem> History => this.agentChat.History;

    public ReadOnlyObservableCollection<AgentChatRunningItem> RunningItems => this.agentChat.RunningItems;

    public ObservableCollection<AgentChatToolViewModel> Tools { get; } = [];

    public ObservableCollection<AgentEditorNavigationItemViewModel> EditorItems { get; }

    public FlowDocument OutputDocument { get; private set; }

    public AgentEditorNavigationItemViewModel? SelectedEditorItem
    {
        get => this.selectedEditorItem;
        set
        {
            if (!this.SetProperty(ref this.selectedEditorItem, value))
            {
                return;
            }

            if (ReferenceEquals(value?.DetailContent, this.toolsDetail))
            {
                this.toolsDetail.SetRootItem(value);
            }

            this.RaisePropertyChanged(nameof(this.SelectedEditorDetailContent));
        }
    }

    public object? SelectedEditorDetailContent => this.SelectedEditorItem?.DetailContent;

    public bool IsReasoningVisible
    {
        get => this.isReasoningVisible;
        private set => this.SetProperty(ref this.isReasoningVisible, value);
    }

    public void ToggleReasoningVisibility() => this.SetReasoningVisibility(!this.IsReasoningVisible);

    public void SetReasoningVisibility(bool visible)
    {
        if (!this.SetProperty(ref this.isReasoningVisible, visible))
        {
            return;
        }

        this.historyDocumentModel?.Refresh();
        this.runningDocumentModel?.Refresh();
    }

    private void OnAgentSessionIdChanged(object? sender, string sessionId)
    {
        this.AgentSessionId = sessionId;
    }

    private void OnToolsChanged(object? sender, EventArgs e)
        => this.ApplyToolSnapshot(this.agentChat.GetToolSnapshot());

    private void ApplyToolSnapshot(IReadOnlyList<AgentChatToolItem> tools)
    {
        this.Tools.Clear();
        foreach (var tool in tools)
        {
            this.Tools.Add(this.CreateToolViewModel(tool));
        }

        this.BuildEditorTree();
    }

    private AgentChatToolViewModel CreateToolViewModel(AgentChatToolItem tool)
        => new(
            tool.Id,
            tool.Name,
            tool.Description,
            tool.Instructions,
            tool.Kind,
            tool.IsEnabled,
            tool.Status,
            tool.Children.Select(this.CreateToolViewModel).ToArray(),
            enabled => this.agentChat.SetToolEnabledAsync(tool.Id, enabled));

    private void BuildEditorTree()
    {
        var selectedId = this.SelectedEditorItem?.Id;
        this.EditorItems.Clear();

        var toolNavigationItems = BuildToolNavigationItems(this.Tools);

        var root = new AgentEditorNavigationItemViewModel(
            "chat",
            this.DisplayName,
            null,
            null,
            null,
            this.conversationDetail,
            [
                new AgentEditorNavigationItemViewModel("chat-details", "Chat details", null, "Session information", null, this.chatDetailsDetail, []),
                new AgentEditorNavigationItemViewModel("chat-tools", "Tools", null, "Loaded tools", null, this.toolsDetail, toolNavigationItems, isExpanded: true),
                new AgentEditorNavigationItemViewModel("chat-background-tasks", "Background tasks", null, "Planned background work", null, this.backgroundTasksDetail, []),
                new AgentEditorNavigationItemViewModel("chat-sub-agents", "Sub-agents", null, "Planned sub-agent work", null, this.subAgentsDetail, []),
            ],
            isExpanded: true);

        this.toolsDetail.SetToolNavigationItems(toolNavigationItems);
        this.EditorItems.Add(root);
        this.SelectedEditorItem = FindNavigationItem(root, selectedId) ?? root;
    }

    private IReadOnlyList<AgentEditorNavigationItemViewModel> BuildToolNavigationItems(IEnumerable<AgentChatToolViewModel> tools)
        => tools.Select(this.BuildToolNavigationItem).ToArray();

    private AgentEditorNavigationItemViewModel BuildToolNavigationItem(AgentChatToolViewModel tool)
        => new(
            tool.Id,
            tool.Name,
            tool.Id,
            tool.Summary,
            tool,
            this.toolsDetail,
            tool.Children.Select(this.BuildToolNavigationItem).ToArray());

    private static AgentEditorNavigationItemViewModel? FindNavigationItem(AgentEditorNavigationItemViewModel root, string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        if (string.Equals(root.Id, id, StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        foreach (var child in root.Children)
        {
            var match = FindNavigationItem(child, id);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        this.historyDocumentModel?.Dispose();
        this.runningDocumentModel?.Dispose();

        this.InputQueue.Dispose();
        this.agentChat.AgentSessionIdChanged -= this.OnAgentSessionIdChanged;
        this.agentChat.ToolsChanged -= this.OnToolsChanged;
        await this.agentChat.DisposeAsync();
    }

    private void AttachOutputDocumentModels()
    {
        this.OutputDocument.Blocks.Add(this.outputHistoryRootSection);
        this.OutputDocument.Blocks.Add(this.outputRunningRootSection);
        this.historyDocumentModel = new ChatHistoryDocumentModel(this.outputHistoryRootSection, this.History, () => this.IsReasoningVisible);
        this.runningDocumentModel = new RunningChatItemsDocumentModel(this.outputRunningRootSection, this.RunningItems, () => this.IsReasoningVisible);
    }
}
