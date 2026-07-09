using AgentSchema;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows.Input;
using Avalonia.Threading;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels.Collections;
using Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.SlashCommands;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class AgentViewModel : ViewModelBase, IAutoScrollViewModel, IAsyncDisposable
{
    private readonly AgentChat agentChat;
    private readonly ObservableLoggerFactory loggerFactory;
    private readonly AgentChatConversationDetailViewModel conversationDetail;
    private readonly AgentChatDetailsViewModel chatDetailsDetail;
    private readonly AgentChatToolsDetailViewModel toolsDetail;
    private readonly AgentChatPlaceholderDetailViewModel backgroundTasksDetail;
    private readonly SubAgentBrowserViewModel subAgentsBrowserDetail;
    private readonly SubAgentsContainerViewModel subAgentsContainerDetail;
    private readonly DiagnosticInspectorViewModel diagnosticsDetail;
    private readonly List<AgentViewModel> subAgentViewModels = [];
    private readonly ObservableCollection<IRunningSubAgentDisplay> subAgentDisplayItems = [];
    private readonly ObservableCollection<DetailContentSlot> detailContentSlots = [];
    private readonly AgentEditorNavigationItemViewModel chatDetailsNavItem;
    private readonly AgentEditorNavigationItemViewModel toolsNavItem;
    private readonly AgentEditorNavigationItemViewModel backgroundTasksNavItem;
    private readonly AgentEditorNavigationItemViewModel subAgentsNavItem;
    private readonly AgentEditorNavigationItemViewModel diagnosticsNavItem;
    private readonly ToolsCollectionTransformer toolsTransformer;
    private readonly SubAgentsCollectionTransformer subAgentsTransformer;
    private bool isReasoningVisible;
    private bool isDiagnosticsVisible;
    private bool autoScrollEnabled = true;
    private bool showChatInputHelpText = true;
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
        this.subAgentsBrowserDetail = new SubAgentBrowserViewModel(agentChat.SubAgents);
        this.subAgentsContainerDetail = new SubAgentsContainerViewModel(this.subAgentsBrowserDetail);
        this.diagnosticsDetail = new DiagnosticInspectorViewModel(agentChat.History);
        this.SubAgentDisplays = new ReadOnlyObservableCollection<IRunningSubAgentDisplay>(this.subAgentDisplayItems);
        this.InterruptCommand = new RelayCommand(agentChat.Interrupt);
        this.ToggleReasoningVisibilityCommand = new RelayCommand(this.ToggleReasoningVisibility);
        this.RequestOpenLogWindowCommand = new RelayCommand(this.RequestOpenLogWindow);
        this.InputQueue = new InputQueueViewModel(
            this.agentChat,
            this.agentChat.DefaultInputQueue,
            this.agentChat.InputQueueManager);
        this.EditorItems = [];

        this.NavigateToAgentHandler = this.NavigateToSubAgent;

        this.agentChat.AgentSessionIdChanged += this.OnAgentSessionIdChanged;
        this.agentChat.ToolsChanged += this.OnToolsChanged;
        this.agentChat.UsageChanged += this.OnUsageChanged;
        if (this.RunningItems is INotifyCollectionChanged runningItemsNotifications)
        {
            runningItemsNotifications.CollectionChanged += this.OnRunningItemsCollectionChanged;
        }
        ((INotifyCollectionChanged)agentChat.SubAgents).CollectionChanged += this.OnSubAgentsCollectionChanged;

        // Create detail content slots.
        this.detailContentSlots.Add(new DetailContentSlot(this.conversationDetail) { IsVisible = true });
        this.detailContentSlots.Add(new DetailContentSlot(this.chatDetailsDetail));
        this.detailContentSlots.Add(new DetailContentSlot(this.toolsDetail));
        this.detailContentSlots.Add(new DetailContentSlot(this.backgroundTasksDetail));
        this.detailContentSlots.Add(new DetailContentSlot(this.subAgentsContainerDetail));
        this.detailContentSlots.Add(new DetailContentSlot(this.diagnosticsDetail));
        this.DetailContentSlots = new ReadOnlyObservableCollection<DetailContentSlot>(this.detailContentSlots);

        // Build fixed navigation items once.
        this.chatDetailsNavItem = new AgentEditorNavigationItemViewModel(
            "chat-details",
            "Chat details",
            null,
            "Session information",
            null,
            this.chatDetailsDetail,
            []);

        this.toolsNavItem = new AgentEditorNavigationItemViewModel(
            "chat-tools",
            "Tools",
            null,
            "Loaded tools",
            null,
            this.toolsDetail,
            [],
            isExpanded: true);

        this.backgroundTasksNavItem = new AgentEditorNavigationItemViewModel(
            "chat-background-tasks",
            "Background tasks",
            null,
            "Planned background work",
            null,
            this.backgroundTasksDetail,
            []);

        this.subAgentsNavItem = new AgentEditorNavigationItemViewModel(
            "chat-sub-agents",
            "Sub-agents",
            null,
            "Sub-agents",
            null,
            this.subAgentsContainerDetail,
            []);

        this.diagnosticsNavItem = new AgentEditorNavigationItemViewModel(
            "chat-diagnostics",
            "Diagnostics",
            null,
            "Diagnostic information",
            null,
            this.diagnosticsDetail,
            []);

        var root = new AgentEditorNavigationItemViewModel(
            "chat",
            this.DisplayName,
            null,
            null,
            null,
            this.conversationDetail,
            [this.chatDetailsNavItem, this.toolsNavItem, this.backgroundTasksNavItem, this.subAgentsNavItem, this.diagnosticsNavItem],
            isExpanded: false);

        this.EditorItems.Add(root);
        this.SelectedEditorItem = root;

        // Set up tools transformer.
        this.toolsTransformer = new ToolsCollectionTransformer(this.Tools, this.toolsNavItem.Children, this.toolsDetail);

        // Set up sub-agents transformer.
        this.subAgentsTransformer = new SubAgentsCollectionTransformer(
            this.subAgentsContainerDetail.Slots,
            this.subAgentsNavItem.Children,
            this.subAgentsNavItem);

        // Seed slots for any sub-agents already present (e.g. restored from persistence).
        foreach (var subAgent in agentChat.SubAgents)
        {
            this.AddSubAgentSlot(subAgent);
        }

        this.ApplyToolSnapshot(agentChat.GetToolSnapshot());
    }

    public string DisplayName { get; }

    public ObservableLoggerFactory LoggerFactory => this.loggerFactory;

    public event EventHandler<bool>? AltKeyStateChanged;
    public event EventHandler<int>? GoToTabAtIndexRequested;

    public void RaiseAltKeyStateChanged(bool isAltHeld)
    {
        this.AltKeyStateChanged?.Invoke(this, isAltHeld);
    }

    public void RaiseGoToTabAtIndex(int index)
    {
        this.GoToTabAtIndexRequested?.Invoke(this, index);
    }

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

    /// <summary>Whether this agent accepts user input (false for hosted sub-agents).</summary>
    public bool AcceptsUserInput => this.agentChat.AcceptsUserInput;

    /// <summary>The sub-agents container (browser card + cached sub-agent slots).</summary>
    public SubAgentsContainerViewModel SubAgentsContainer => this.subAgentsContainerDetail;

    /// <summary>UI-layer display wrappers for each direct child sub-agent, in the order they were created.</summary>
    public ReadOnlyObservableCollection<IRunningSubAgentDisplay> SubAgentDisplays { get; }

    public ICommand InterruptCommand { get; }

    public ICommand ToggleReasoningVisibilityCommand { get; }

    public ICommand RequestOpenLogWindowCommand { get; }

    public InputQueueViewModel InputQueue { get; }

    public DiagnosticInspectorViewModel DiagnosticsInspector => this.diagnosticsDetail;

    public ReadOnlyObservableCollection<AgentChatHistoryItem> History => this.agentChat.History;

    public ReadOnlyObservableCollection<AgentChatRunningItem> RunningItems => this.agentChat.RunningItems;

    public ObservableCollection<AgentChatToolViewModel> Tools { get; } = [];

    public ObservableCollection<AgentEditorNavigationItemViewModel> EditorItems { get; }

    public ReadOnlyObservableCollection<DetailContentSlot> DetailContentSlots { get; }

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

            // Update the sub-agents container when a sub-agent nav item is selected.
            if (value is not null && ReferenceEquals(value.DetailContent, this.subAgentsContainerDetail))
            {
                if (value.Id == "chat-sub-agents")
                {
                    // The group node itself — show the browser card.
                    this.subAgentsContainerDetail.ShowBrowser();
                }
                else if (value.Id.StartsWith("sub-agent-", StringComparison.Ordinal))
                {
                    // An individual sub-agent child node.
                    var agentId = value.Id.Substring("sub-agent-".Length);
                    this.subAgentsContainerDetail.ShowSubAgent(agentId);
                }
            }

            // Update detail content slot visibility.
            var selected = value?.DetailContent;
            foreach (var slot in this.detailContentSlots)
            {
                slot.IsVisible = ReferenceEquals(slot.Content, selected);
            }
        }
    }

    public object? SelectedEditorDetailContent => this.SelectedEditorItem?.DetailContent;

    public bool IsReasoningVisible
    {
        get => this.isReasoningVisible;
        private set => this.SetProperty(ref this.isReasoningVisible, value);
    }

    public bool IsDiagnosticsVisible
    {
        get => this.isDiagnosticsVisible;
        private set => this.SetProperty(ref this.isDiagnosticsVisible, value);
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

    public bool ShowChatInputHelpText
    {
        get => this.showChatInputHelpText;
        set
        {
            if (this.SetProperty(ref this.showChatInputHelpText, value))
            {
                this.InputQueue.DefaultComposer.ShowChatInputHelpText = value;
            }
        }
    }

    public IAgentStatusSink StatusSink => this.conversationDetail.StatusLine;

    public void ToggleReasoningVisibility() => this.SetReasoningVisibility(!this.IsReasoningVisible);

    public void ToggleDiagnosticsVisibility() => this.SetDiagnosticsVisibility(!this.IsDiagnosticsVisible);

    public event EventHandler? OpenLogWindowRequested;

    /// <summary>
    /// Configures the slash command context factory for this view model.
    /// The context factory produces a <see cref="SlashCommandContext"/> for each command
    /// invocation; available commands are read from <see cref="AgentChat.SlashCommands"/>.
    /// </summary>
    public void ConfigureSlashCommands(Func<SlashCommandContext> contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);

        this.InputQueue.DefaultComposer.SlashCommandInterceptorAsync = text =>
            this.RunSlashCommandAsync(contextFactory, text);

        var rootHandler = new RootSlashCommandCompletionsHandler(this.agentChat.SlashCommands);
        this.InputQueue.DefaultComposer.SlashCompletionsProviderAsync = async (commandName, partialInput, ct) =>
        {
            if (string.IsNullOrEmpty(commandName))
            {
                // Root case: user is still typing the command name (no space yet).
                // partialInput is the partial command name (or empty string for just "/").
                return rootHandler.GetCompletions(partialInput);
            }

            var handler = this.agentChat.SlashCommands.Commands.FirstOrDefault(
                c => string.Equals(c.Name, commandName, StringComparison.OrdinalIgnoreCase));

            if (handler is null)
            {
                return Array.Empty<SlashCommandCompletion>();
            }

            var context = contextFactory();
            var completions = await handler.GetCompletionsAsync(context, partialInput, ct);
            return completions
                .OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        };

        ((SlashCommandRegistry)this.agentChat.SlashCommands).Register(new AutoResumeSlashCommandHandler());
        ((SlashCommandRegistry)this.agentChat.SlashCommands).Register(new InputHelpSlashCommandHandler(
            getValue: () => this.ShowChatInputHelpText,
            setValue: v => this.ShowChatInputHelpText = v));
        ((SlashCommandRegistry)this.agentChat.SlashCommands).Register(new DiagnosticsSlashCommandHandler(
            getValue: () => this.IsDiagnosticsVisible,
            setValue: v => this.SetDiagnosticsVisibility(v)));
        ((SlashCommandRegistry)this.agentChat.SlashCommands).Register(new ReasoningSlashCommandHandler(
            getValue: () => this.IsReasoningVisible,
            setValue: v => this.SetReasoningVisibility(v)));
    }

    private async Task RunSlashCommandAsync(
        Func<SlashCommandContext> contextFactory,
        string text)
    {
        // text starts with "/" — parse "/<name> [args]"
        var afterSlash = text.Substring(1);
        var spaceIndex = afterSlash.IndexOf(' ');
        var commandName = spaceIndex < 0 ? afterSlash : afterSlash.Substring(0, spaceIndex);
        var arguments = spaceIndex < 0 ? string.Empty : afterSlash.Substring(spaceIndex + 1).Trim();

        var commands = this.agentChat.SlashCommands.Commands;
        var handler = commands.FirstOrDefault(
            c => string.Equals(c.Name, commandName, StringComparison.OrdinalIgnoreCase));

        if (handler is null)
        {
            // Unknown slash command — forward to the LLM as a plain message.
            this.agentChat.EnqueueUserMessage(text);
            return;
        }

        var context = contextFactory();
        SlashCommandResult result;
        try
        {
            result = await handler.ExecuteAsync(context, arguments, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            result = new SlashCommandResult
            {
                StatusMessage = $"Command /{commandName} failed: {exception.Message}",
            };
        }

        // Show the status message as a system note in the chat history so the user
        // gets feedback without the message being forwarded to the LLM.
        this.agentChat.EnqueueSystemNote(result.StatusMessage);
    }

    /// <summary>
    /// Called when the user activates a hyperlink in the chat output. The workspace layer
    /// sets this to open a browser tab; the standalone Agent.Gui app sets it to
    /// <see cref="System.Diagnostics.Process.Start"/>. Null means no navigation occurs.
    /// </summary>
    public Action<string>? OpenUrlHandler { get; set; }

    /// <summary>
    /// Called when the user clicks the '→ Open sub-agent' jump link on a tool-result block.
    /// The argument is the <see cref="AgentChat.AgentId"/> of the target sub-agent.
    /// Implemented in the workspace layer (issue #634). Null means no navigation occurs.
    /// </summary>
    public Action<string>? NavigateToAgentHandler { get; set; }

    private void RequestOpenLogWindow()
        => this.OpenLogWindowRequested?.Invoke(this, EventArgs.Empty);

    public void SetReasoningVisibility(bool visible)
        => this.IsReasoningVisible = visible;

    public void SetDiagnosticsVisibility(bool visible)
        => this.IsDiagnosticsVisible = visible;

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

    public async ValueTask DisposeAsync()
    {
        await this.DisposeViewResourcesAsync();
        await this.agentChat.DisposeAsync();
    }

    public async ValueTask DisposeViewResourcesAsync()
    {
        this.toolsTransformer.Dispose();
        this.subAgentsTransformer.Dispose();
        this.InputQueue.Dispose();
        this.conversationDetail.Dispose();
        this.diagnosticsDetail.Dispose();
        this.subAgentsBrowserDetail.Dispose();
        ((INotifyCollectionChanged)this.agentChat.SubAgents).CollectionChanged -= this.OnSubAgentsCollectionChanged;
        this.agentChat.AgentSessionIdChanged -= this.OnAgentSessionIdChanged;
        this.agentChat.ToolsChanged -= this.OnToolsChanged;
        this.agentChat.UsageChanged -= this.OnUsageChanged;
        if (this.RunningItems is INotifyCollectionChanged runningItemsNotifications)
        {
            runningItemsNotifications.CollectionChanged -= this.OnRunningItemsCollectionChanged;
        }
        foreach (var subAgentViewModel in this.subAgentViewModels)
        {
            await subAgentViewModel.DisposeViewResourcesAsync();
        }

        foreach (var display in this.subAgentDisplayItems)
        {
            if (display is IDisposable d)
                d.Dispose();
        }

        await Task.CompletedTask;
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

    private void OnSubAgentsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (IRunningSubAgent subAgent in e.NewItems)
            {
                this.AddSubAgentSlot(subAgent);
            }
        }
    }

    private void AddSubAgentSlot(IRunningSubAgent subAgent)
    {
        // Extract the underlying AgentChat from both AgentChat instances and SubAgent wrappers.
        AgentChat? subAgentChat = subAgent switch
        {
            AgentChat directChat => directChat,
            SubAgent wrapper => wrapper.AgentChat,
            _ => null
        };

        if (subAgentChat is null)
        {
            // Skip lazily-restored SubAgent wrappers that haven't been loaded yet.
            return;
        }

        var display = new RunningSubAgentDisplay(subAgentChat);
        this.subAgentDisplayItems.Add(display);
        var subAgentViewModel = new AgentViewModel(subAgentChat, subAgent.DisplayName, this.loggerFactory);
        this.subAgentViewModels.Add(subAgentViewModel);
        this.subAgentsContainerDetail.AddSlot(subAgent.AgentId, subAgentViewModel);
    }

    private void NavigateToSubAgent(string agentId)
    {
        // Select the "Sub-agents" group node in the editor tree so the container is shown,
        // then tell the container to display the requested sub-agent.
        if (this.EditorItems.Count == 0)
        {
            return;
        }

        var root = this.EditorItems[0];
        var subAgentsGroup = root.Children.FirstOrDefault(c => c.Id == "chat-sub-agents");
        if (subAgentsGroup is null)
        {
            return;
        }

        // Select the child nav item for this agent (or fall back to the group itself).
        var childItem = subAgentsGroup.Children.FirstOrDefault(c =>
            c.Id == $"sub-agent-{agentId}");

        this.SelectedEditorItem = childItem ?? subAgentsGroup;
        subAgentsGroup.IsExpanded = true;

        // Ensure the container shows the requested sub-agent.
        this.subAgentsContainerDetail.ShowSubAgent(agentId);
    }

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

    private sealed class ToolsCollectionTransformer : CollectionTransformer<AgentChatToolViewModel, AgentEditorNavigationItemViewModel>
    {
        private readonly AgentChatToolsDetailViewModel toolsDetail;

        public ToolsCollectionTransformer(
            IReadOnlyList<AgentChatToolViewModel> source,
            IList<AgentEditorNavigationItemViewModel> target,
            AgentChatToolsDetailViewModel toolsDetail)
            : base(source, target)
        {
            this.toolsDetail = toolsDetail;
            this.ApplyInitialTransform();
            this.toolsDetail.SetToolNavigationItems((ObservableCollection<AgentEditorNavigationItemViewModel>)target);
        }

        protected override AgentEditorNavigationItemViewModel Create(AgentChatToolViewModel tool)
            => this.BuildToolNavigationItem(tool, isTopLevel: true);

        private AgentEditorNavigationItemViewModel BuildToolNavigationItem(AgentChatToolViewModel tool, bool isTopLevel = false)
            => new(
                tool.Id,
                tool.Name,
                tool.Id,
                tool.Summary,
                tool,
                this.toolsDetail,
                tool.Children.Select(c => this.BuildToolNavigationItem(c, isTopLevel: false)).ToArray(),
                isExpanded: !isTopLevel);
    }

    private sealed class SubAgentsCollectionTransformer : CollectionTransformer<SubAgentSlotViewModel, AgentEditorNavigationItemViewModel>
    {
        private readonly AgentEditorNavigationItemViewModel subAgentsNavItem;

        public SubAgentsCollectionTransformer(
            IReadOnlyList<SubAgentSlotViewModel> source,
            IList<AgentEditorNavigationItemViewModel> target,
            AgentEditorNavigationItemViewModel subAgentsNavItem)
            : base(source, target)
        {
            this.subAgentsNavItem = subAgentsNavItem;
            this.ApplyInitialTransform();
            this.UpdateSubAgentsLabel();
        }

        protected override AgentEditorNavigationItemViewModel Create(SubAgentSlotViewModel slot)
        {
            var subRoot = slot.SubAgentViewModel.EditorItems.FirstOrDefault();
            return new AgentEditorNavigationItemViewModel(
                $"sub-agent-{slot.AgentId}",
                slot.SubAgentViewModel.DisplayName,
                null,
                null,
                null,
                this.subAgentsNavItem.DetailContent,
                subRoot?.Children.ToArray() ?? []);
        }

        protected override void OnInsert(int index, AgentEditorNavigationItemViewModel target)
        {
            this.UpdateSubAgentsLabel();
            if (this.Target.Count == 1)
            {
                this.subAgentsNavItem.IsExpanded = true;
            }
        }

        protected override void OnRemoveAt(int index, AgentEditorNavigationItemViewModel target)
        {
            this.UpdateSubAgentsLabel();
        }

        private void UpdateSubAgentsLabel()
        {
            var count = this.Target.Count;
            this.subAgentsNavItem.Name = count > 0 ? $"Sub-agents ({count})" : "Sub-agents";
        }
    }
}
