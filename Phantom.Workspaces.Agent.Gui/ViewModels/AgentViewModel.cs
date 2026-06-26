using AgentSchema;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Threading;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class AgentViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly AgentChat agentChat;
    private readonly ObservableLoggerFactory loggerFactory;
    private readonly AgentChatConversationDetailViewModel conversationDetail;
    private readonly AgentChatDetailsViewModel chatDetailsDetail;
    private readonly AgentChatToolsDetailViewModel toolsDetail;
    private readonly AgentChatPlaceholderDetailViewModel backgroundTasksDetail;
    private readonly AgentChatPlaceholderDetailViewModel subAgentsDetail;
    private bool isReasoningVisible;
    private bool autoScrollEnabled = true;
    private string agentSessionId;
    private AgentEditorNavigationItemViewModel? selectedEditorItem;

    public AgentViewModel(AgentChat agentChat, string displayName, ObservableLoggerFactory loggerFactory)
    {
        this.agentChat = agentChat;
        this.loggerFactory = loggerFactory;
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
        this.ToggleReasoningVisibilityCommand = new RelayCommand(this.ToggleReasoningVisibility);
        this.RequestOpenLogWindowCommand = new RelayCommand(this.RequestOpenLogWindow);
        this.InputQueue = new InputQueueViewModel(
            this.agentChat,
            this.agentChat.DefaultInputQueue,
            this.agentChat.InputQueueManager);
        this.EditorItems = [];

        this.agentChat.AgentSessionIdChanged += this.OnAgentSessionIdChanged;
        this.agentChat.ToolsChanged += this.OnToolsChanged;
        this.agentChat.UsageChanged += this.OnUsageChanged;
        if (this.RunningItems is INotifyCollectionChanged runningItemsNotifications)
        {
            runningItemsNotifications.CollectionChanged += this.OnRunningItemsCollectionChanged;
        }
        this.ApplyToolSnapshot(agentChat.GetToolSnapshot());
    }

    public string DisplayName { get; }

    public ObservableLoggerFactory LoggerFactory => this.loggerFactory;

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

    public string ModelProvider => this.ResolveAgentModel()?.Provider ?? string.Empty;

    public string ModelId => this.ResolveAgentModel()?.Id ?? string.Empty;

    public long? TotalInputTokenCount => this.agentChat.TotalInputTokenCount;

    public long? TotalOutputTokenCount => this.agentChat.TotalOutputTokenCount;

    public string ModelApiType => this.ResolveAgentModel()?.ApiType ?? string.Empty;

    public string ModelConnectionType => this.ResolveAgentModel()?.Connection switch
    {
        null => "(none)",
        ApiKeyConnection => "API key",
        AnonymousConnection => "Anonymous",
        var connection => connection.GetType().Name,
    };

    public AgentChat AgentChat => this.agentChat;

    public ICommand InterruptCommand { get; }

    public ICommand ToggleReasoningVisibilityCommand { get; }

    public ICommand RequestOpenLogWindowCommand { get; }

    public InputQueueViewModel InputQueue { get; }

    public ReadOnlyObservableCollection<AgentChatHistoryItem> History => this.agentChat.History;

    public ReadOnlyObservableCollection<AgentChatRunningItem> RunningItems => this.agentChat.RunningItems;

    public ObservableCollection<AgentChatToolViewModel> Tools { get; } = [];

    public ObservableCollection<AgentEditorNavigationItemViewModel> EditorItems { get; }

    public bool IsChatRunning => this.RunningItems.Count > 0;

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

    public bool AutoScrollEnabled
    {
        get => this.autoScrollEnabled;
        set
        {
            if (this.SetProperty(ref this.autoScrollEnabled, value))
            {
                this.RaisePropertyChanged(nameof(this.AutoScrollDisabled));
            }
        }
    }

    public bool AutoScrollDisabled => !this.autoScrollEnabled;

    public void ToggleReasoningVisibility() => this.SetReasoningVisibility(!this.IsReasoningVisible);

    public event EventHandler? OpenLogWindowRequested;

    /// <summary>
    /// Called when the user activates a hyperlink in the chat output. The workspace layer
    /// sets this to open a browser tab; the standalone Agent.Gui app sets it to
    /// <see cref="System.Diagnostics.Process.Start"/>. Null means no navigation occurs.
    /// </summary>
    public Action<string>? OpenUrlHandler { get; set; }

    private void RequestOpenLogWindow()
        => this.OpenLogWindowRequested?.Invoke(this, EventArgs.Empty);

    public void SetReasoningVisibility(bool visible)
        => this.SetProperty(ref this.isReasoningVisible, visible);

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
            isExpanded: false);

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
        this.InputQueue.Dispose();
        this.conversationDetail.Dispose();
        this.agentChat.AgentSessionIdChanged -= this.OnAgentSessionIdChanged;
        this.agentChat.ToolsChanged -= this.OnToolsChanged;
        this.agentChat.UsageChanged -= this.OnUsageChanged;
        if (this.RunningItems is INotifyCollectionChanged runningItemsNotifications)
        {
            runningItemsNotifications.CollectionChanged -= this.OnRunningItemsCollectionChanged;
        }
        await this.agentChat.DisposeAsync();
    }

    private Model? ResolveAgentModel()
    {
        var agentDefinition = this.agentChat.AgentDefinition;
        if (agentDefinition is null)
        {
            return null;
        }

        return AgentFactory.GetModel(agentDefinition);
    }

    private void OnRunningItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => this.RaisePropertyChanged(nameof(this.IsChatRunning));

    private void OnUsageChanged(object? sender, EventArgs eventArgs)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            this.RaiseUsagePropertiesChanged();
            return;
        }

        Dispatcher.UIThread.Post(this.RaiseUsagePropertiesChanged);
    }

    private void RaiseUsagePropertiesChanged()
    {
        this.RaisePropertyChanged(nameof(this.TotalInputTokenCount));
        this.RaisePropertyChanged(nameof(this.TotalOutputTokenCount));
    }
}
