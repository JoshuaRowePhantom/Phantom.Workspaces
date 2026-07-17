using AgentSchema;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows.Input;
using Avalonia.Threading;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Agent.Gui.ViewModels.Collections;
using Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.SlashCommands;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class AgentViewModel : ViewModelBase, IAutoScrollViewModel, IAsyncDisposable
{
    private readonly AgentChat agentChat;
    private readonly ObservableLoggerFactory loggerFactory;
    private readonly ILogger logger;
    private readonly AgentChatConversationDetailViewModel conversationDetail;
    private readonly AgentChatDetailsViewModel chatDetailsDetail;
    private readonly AgentChatToolsDetailViewModel toolsDetail;
    private readonly SubAgentBrowserViewModel subAgentsBrowserDetail;
    private readonly SubAgentsContainerViewModel subAgentsContainerDetail;
    private readonly List<AgentViewModel> subAgentViewModels = [];
    private readonly List<RunningAgentChatLease> subAgentLeases = [];
    private readonly ObservableCollection<IRunningSubAgentDisplay> subAgentDisplayItems = [];
    private readonly ObservableCollection<AgentDetailDocumentItem> allDetailContents = [];
    private readonly Dictionary<AgentViewModel, NotifyCollectionChangedEventHandler> subAgentDetailSubscriptions = new();
    private readonly ObservableCollection<AgentEditorNavigationItemViewModel> subAgentAllChildren = [];
    private readonly AgentEditorNavigationItemViewModel chatDetailsNavItem;
    private readonly AgentEditorNavigationItemViewModel toolsNavItem;
    private readonly AgentEditorNavigationItemViewModel subAgentsNavItem;
    private readonly ToolsCollectionTransformer toolsTransformer;
    private readonly SubAgentsCollectionTransformer subAgentsTransformer;
    private readonly TaskScheduler foregroundScheduler;
    private bool isReasoningVisible;
    private bool autoScrollEnabled = true;
    private bool showChatInputHelpText = true;
    private string agentSessionId;
    private AgentEditorNavigationItemViewModel? selectedEditorItem;
    private readonly string detailKeyPrefix = System.Guid.NewGuid().ToString("N");
    private readonly AgentDetailDockFactory detailDockFactory;
    private AgentDetailDocumentItem? selectedDetailItem;

    public AgentViewModel(AgentChat agentChat, string displayName, string description, ObservableLoggerFactory loggerFactory, TaskScheduler? foregroundScheduler = null, AgentViewModel? parentAgentViewModel = null)
    {
        this.agentChat = agentChat;
        this.loggerFactory = loggerFactory;
        this.logger = loggerFactory.CreateLogger<AgentViewModel>();
        this.foregroundScheduler = foregroundScheduler ?? TaskScheduler.Default;
        this.agentSessionId = agentChat.AgentSessionId;
        this.ParentAgentViewModel = parentAgentViewModel;
        this.ParentAgentDisplay = parentAgentViewModel is not null
            ? new RunningParentAgentDisplay(parentAgentViewModel.agentChat)
            : null;
        this.DisplayName = displayName;
        this.Description = description;
        this.conversationDetail = new AgentChatConversationDetailViewModel(this);
        this.chatDetailsDetail = new AgentChatDetailsViewModel(this);
        this.toolsDetail = new AgentChatToolsDetailViewModel();
        this.subAgentsBrowserDetail = new SubAgentBrowserViewModel(agentChat.SubAgents);
        this.subAgentsContainerDetail = new SubAgentsContainerViewModel(this.subAgentsBrowserDetail);
        this.SubAgentDisplays = new ReadOnlyObservableCollection<IRunningSubAgentDisplay>(this.subAgentDisplayItems);
        this.InterruptCommand = new RelayCommand(agentChat.Interrupt);
        this.ToggleReasoningVisibilityCommand = new RelayCommand(this.ToggleReasoningVisibility);
        this.RequestOpenLogWindowCommand = new RelayCommand(this.RequestOpenLogWindow);
        this.InputQueue = agentChat.AcceptsUserInput
            ? new InputQueueViewModel(
                this.agentChat,
                this.agentChat.DefaultInputQueue,
                this.agentChat.InputQueueManager)
            : null;
        this.ToggleHoldAllQueuesCommand = new RelayCommand(() => this.InputQueue?.ToggleHoldAllQueuesCommand.Execute(null));
        this.HoldAllQueuesCommand = new RelayCommand(() => this.InputQueue?.HoldAllQueuesCommand.Execute(null));
        this.UnholdAllQueuesCommand = new RelayCommand(() => this.InputQueue?.UnholdAllQueuesCommand.Execute(null));
        this.EditorItems = [];

        this.NavigateToAgentHandler = this.NavigateToAgent;

        this.agentChat.AgentSessionIdChanged += this.OnAgentSessionIdChanged;
        this.agentChat.ToolsChanged += this.OnToolsChanged;
        this.agentChat.UsageChanged += this.OnUsageChanged;
        if (this.RunningItems is INotifyCollectionChanged runningItemsNotifications)
        {
            runningItemsNotifications.CollectionChanged += this.OnRunningItemsCollectionChanged;
        }
        ((INotifyCollectionChanged)agentChat.SubAgents).CollectionChanged += this.OnSubAgentsCollectionChanged;

        // Build the flat detail-content collection (one item per nav node's DetailContent) and the
        // locked, tab-strip-less DocumentDock that hosts them (issue #1035). Every node — including
        // each sub-agent child — contributes a first-class cached document, so no detail panel is
        // ever blank. Sub-agents append their own items recursively (see AddSubAgentSlotEager).
        this.allDetailContents.Add(new AgentDetailDocumentItem($"{this.detailKeyPrefix}/conversation", "Chat", this.conversationDetail));
        this.allDetailContents.Add(new AgentDetailDocumentItem($"{this.detailKeyPrefix}/chat-details", "Chat details", this.chatDetailsDetail));
        this.allDetailContents.Add(new AgentDetailDocumentItem($"{this.detailKeyPrefix}/chat-tools", "Tools", this.toolsDetail));
        this.allDetailContents.Add(new AgentDetailDocumentItem($"{this.detailKeyPrefix}/chat-sub-agents", "Sub-agents", this.subAgentsContainerDetail));
        this.AllDetailContents = new ReadOnlyObservableCollection<AgentDetailDocumentItem>(this.allDetailContents);
        this.detailDockFactory = new AgentDetailDockFactory(this.allDetailContents);

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

        this.subAgentsNavItem = new AgentEditorNavigationItemViewModel(
            "chat-sub-agents",
            "Sub-agents",
            null,
            "Sub-agents",
            null,
            this.subAgentsContainerDetail,
            [],
            isExpanded: true,
            showHideCompletedToggle: true);

        var root = new AgentEditorNavigationItemViewModel(
            "chat",
            this.DisplayName,
            null,
            null,
            null,
            this.conversationDetail,
            [this.chatDetailsNavItem, this.toolsNavItem, this.subAgentsNavItem],
            isExpanded: false);

        this.EditorItems.Add(root);
        this.SelectedEditorItem = root;

        // Set up tools transformer.
        this.toolsTransformer = new ToolsCollectionTransformer(this.Tools, this.toolsNavItem.Children, this.toolsDetail);

        // Set up sub-agents transformer. The transformer maintains the full (unfiltered) set of
        // sub-agent nav items in subAgentAllChildren and projects a completion-filtered view into
        // subAgentsNavItem.Children (see issue #1033).
        this.subAgentsTransformer = new SubAgentsCollectionTransformer(
            this.subAgentsContainerDetail.Slots,
            this.subAgentAllChildren,
            this.subAgentsNavItem);

        // Seed slots for any sub-agents already present (e.g. restored from persistence).
        foreach (var subAgent in agentChat.SubAgents)
        {
            this.AddSubAgentSlot(subAgent);
        }

        this.ApplyToolSnapshot(agentChat.GetToolSnapshot());
    }

    public string DisplayName { get; }

    public string Description { get; }

    public AgentChatConversationDetailViewModel ConversationDetail => this.conversationDetail;

    public ObservableLoggerFactory LoggerFactory => this.loggerFactory;

    public event EventHandler<bool>? AltKeyStateChanged;
    public event EventHandler<int>? GoToTabAtIndexRequested;
    public event EventHandler<int>? GoToWorkspacePaneAtIndexRequested;

    public void RaiseAltKeyStateChanged(bool isAltHeld)
    {
        this.AltKeyStateChanged?.Invoke(this, isAltHeld);
    }

    public void RaiseGoToTabAtIndex(int index)
    {
        this.GoToTabAtIndexRequested?.Invoke(this, index);
    }

    public void RaiseGoToWorkspacePaneAtIndex(int index)
    {
        this.GoToWorkspacePaneAtIndexRequested?.Invoke(this, index);
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

    /// <summary>The parent agent's view model, or <see langword="null"/> for root agents.</summary>
    public AgentViewModel? ParentAgentViewModel { get; }

    /// <summary>
    /// Display wrapper for this agent's parent, used to render the [Parent agent] panel above the
    /// [Running sub-agents] panel. <see langword="null"/> for root agents.
    /// </summary>
    public IRunningSubAgentDisplay? ParentAgentDisplay { get; }

    public ICommand InterruptCommand { get; }

    public ICommand ToggleReasoningVisibilityCommand { get; }

    public ICommand RequestOpenLogWindowCommand { get; }

    public ICommand ToggleHoldAllQueuesCommand { get; }

    public ICommand HoldAllQueuesCommand { get; }

    public ICommand UnholdAllQueuesCommand { get; }

    public InputQueueViewModel? InputQueue { get; }

    public ReadOnlyObservableCollection<AgentChatHistoryItem> History => this.agentChat.History;

    /// <summary>
    /// Completes once the underlying <see cref="AgentChat"/> has loaded persisted history into
    /// <see cref="History"/>. The chat output control awaits this before taking its initial history
    /// snapshot so first-open never renders an empty history (issue #1009). Tests may override this
    /// via <see cref="SetHistoryPopulatedForTest"/> to simulate a still-loading session.
    /// </summary>
    public Task HistoryPopulated => this.historyPopulatedOverride ?? this.agentChat.HistoryPopulated;

    private Task? historyPopulatedOverride;

    /// <summary>Test seam: force <see cref="HistoryPopulated"/> to track a caller-controlled task.</summary>
    internal void SetHistoryPopulatedForTest(Task historyPopulated)
        => this.historyPopulatedOverride = historyPopulated;

    public ReadOnlyObservableCollection<AgentChatRunningItem> RunningItems => this.agentChat.RunningItems;

    public ObservableCollection<AgentChatToolViewModel> Tools { get; } = [];

    public ObservableCollection<AgentEditorNavigationItemViewModel> EditorItems { get; }

    public ReadOnlyObservableCollection<AgentDetailDocumentItem> AllDetailContents { get; }

    /// <summary>
    /// The root Dock layout bound to the detail region's <c>DockControl.Layout</c> (issue #1035).
    /// Hosts the locked, tab-strip-less <see cref="AgentDetailDocumentDock"/> whose cached documents
    /// are generated from <see cref="AllDetailContents"/>.
    /// </summary>
    public Dock.Model.Controls.IRootDock DetailLayout => this.detailDockFactory.Layout;

    /// <summary>The cached detail document currently active in the detail dock, or null.</summary>
    public AgentDetailDocument? SelectedDetailDocument => this.detailDockFactory.GetDocument(this.selectedDetailItem);

    /// <summary>Test/host seam: the factory that owns the detail dock and its document registry.</summary>
    internal AgentDetailDockFactory DetailDockFactory => this.detailDockFactory;

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

            // Activate the cached detail document whose content matches the selected node
            // (issue #1035). Replaces the old ReferenceEquals slot-visibility toggle; every node —
            // including sub-agent children — resolves to a first-class document, so nothing blanks.
            var selectedContent = value?.DetailContent;
            AgentDetailDocumentItem? item = null;
            foreach (var candidate in this.allDetailContents)
            {
                if (ReferenceEquals(candidate.Content, selectedContent))
                {
                    item = candidate;
                    break;
                }
            }

            this.selectedDetailItem = item;
            this.detailDockFactory.SetActiveDetail(item);
            this.RaisePropertyChanged(nameof(this.SelectedDetailDocument));
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

    public bool ShowChatInputHelpText
    {
        get => this.showChatInputHelpText;
        set
        {
            if (this.SetProperty(ref this.showChatInputHelpText, value))
            {
                if (this.InputQueue is not null)
                {
                    this.InputQueue.DefaultComposer.ShowChatInputHelpText = value;
                }
            }
        }
    }

    public IAgentStatusSink StatusSink => this.conversationDetail.StatusLine;

    public void ToggleReasoningVisibility() => this.SetReasoningVisibility(!this.IsReasoningVisible);

    public event EventHandler? OpenLogWindowRequested;

    /// <summary>
    /// Configures the slash command context factory for this view model.
    /// The context factory produces a <see cref="SlashCommandContext"/> for each command
    /// invocation; available commands are read from <see cref="AgentChat.SlashCommands"/>.
    /// </summary>
    public void ConfigureSlashCommands(Func<SlashCommandContext> contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);

        if (this.InputQueue is null)
        {
            return;
        }

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

        this.agentChat.SlashCommands.Register(new AutoResumeSlashCommandHandler());
        this.agentChat.SlashCommands.Register(new InputHelpSlashCommandHandler(
            getValue: () => this.ShowChatInputHelpText,
            setValue: v => this.ShowChatInputHelpText = v));
        this.agentChat.SlashCommands.Register(new ReasoningSlashCommandHandler(
            getValue: () => this.IsReasoningVisible,
            setValue: v => this.SetReasoningVisibility(v)));
        this.agentChat.SlashCommands.Register(new RestartSlashCommandHandler());
        this.agentChat.SlashCommands.Register(new CloneSlashCommandHandler());
        this.agentChat.SlashCommands.Register(new RenameSlashCommandHandler());
        this.agentChat.SlashCommands.Register(new TitleSlashCommandHandler());
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
                IsTransient = false,
            };
        }

        // Transient results are displayed as a one-off inline notification rather than
        // being persisted into conversation history.
        if (result.IsTransient)
        {
            this.agentChat.RaiseTransientNotification(result.StatusMessage);
            return;
        }

        // Show the status message as a system note in the chat history so the user
        // gets feedback without the message being forwarded to the LLM.
        if (result.Role == AgentChatHistoryItem.HelpChatRole)
        {
            this.agentChat.EnqueueHelpNote(result.StatusMessage);
        }
        else
        {
            this.agentChat.EnqueueSystemNote(result.StatusMessage);
        }
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
        foreach (var (subAgentViewModel, handler) in this.subAgentDetailSubscriptions)
        {
            ((INotifyCollectionChanged)subAgentViewModel.AllDetailContents).CollectionChanged -= handler;
        }
        this.subAgentDetailSubscriptions.Clear();
        this.InputQueue?.Dispose();
        this.conversationDetail.Dispose();
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

        if (this.ParentAgentDisplay is IDisposable parentDisplayDisposable)
        {
            parentDisplayDisposable.Dispose();
        }

        // Dispose sub-agent leases
        foreach (var lease in this.subAgentLeases)
        {
            await lease.DisposeAsync();
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
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            this.RaisePropertyChanged(nameof(this.IsChatRunning));
            return;
        }

        Dispatcher.UIThread.Post(() => this.RaisePropertyChanged(nameof(this.IsChatRunning)));
    }

    private void OnSubAgentsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (IRunningSubAgent subAgent in e.NewItems)
            {
                this.AddSubAgentSlot(subAgent);
            }
        }

        if (e.OldItems is not null)
        {
            foreach (IRunningSubAgent subAgent in e.OldItems)
            {
                this.RemoveSubAgentDetailContents(subAgent.AgentId);
            }
        }
    }

    private void AddSubAgentSlot(IRunningSubAgent subAgent)
    {
        // Handle both AgentChat (eager path from GetOrCreateAsync) and SubAgent (lazy path from RestoreSubAgentsAsync)
        if (subAgent is AgentChat agentChat)
        {
            // Eager path: AgentChat added directly by GetOrCreateAsync
            this.AddSubAgentSlotEager(subAgent, agentChat);
            return;
        }

        if (subAgent is SubAgent sub)
        {
            var subAgentChat = sub.AgentChat;
            if (subAgentChat is null)
            {
                // Lazy/restored path — materialise asynchronously
                this.AddSubAgentSlotLazy(sub);
                return;
            }
            // Eager path with SubAgent wrapper
            this.AddSubAgentSlotEager(sub, subAgentChat);
            return;
        }

        this.logger.LogWarning("Unknown IRunningSubAgent type: {Type}", subAgent.GetType().Name);
    }

    private void AddSubAgentSlotEager(IRunningSubAgent subAgent, AgentChat subAgentChat)
    {
        var display = new RunningSubAgentDisplay(subAgentChat);
        this.subAgentDisplayItems.Add(display);
        var subAgentViewModel = new AgentViewModel(subAgentChat, subAgent.DisplayName, subAgent.Description, this.loggerFactory, this.foregroundScheduler, this);
        // Delegate the sub-agent's navigation handler to this parent so ancestor navigation works
        // (issue #1046): the parent can resolve its own children, and if the target is above this
        // agent it falls through to ancestor resolution logic in NavigateToSubAgent.
        subAgentViewModel.NavigateToAgentHandler = this.NavigateToAgentHandler;
        this.subAgentViewModels.Add(subAgentViewModel);
        // Recursively aggregate the sub-agent's flat detail-content collection into this agent's
        // collection so every sub-agent node (and its descendants) has a first-class cached document
        // in the root dock (issue #1035). The sub-agent's collection already includes its own
        // sub-agents, so arbitrary nesting depth is handled without special-casing.
        this.AppendSubAgentDetailContents(subAgentViewModel);
        // Use the AgentChat's AgentId, not the stub's AgentId (which may be the session ID for lazy stubs)
        this.subAgentsContainerDetail.AddSlot(subAgentChat.AgentId, subAgentViewModel, subAgentChat);
    }

    private void AppendSubAgentDetailContents(AgentViewModel subAgentViewModel)
    {
        foreach (var item in subAgentViewModel.AllDetailContents)
        {
            if (!this.allDetailContents.Contains(item))
            {
                this.allDetailContents.Add(item);
            }
        }

        NotifyCollectionChangedEventHandler handler = (_, e) => this.OnSubAgentDetailContentsChanged(e);
        ((INotifyCollectionChanged)subAgentViewModel.AllDetailContents).CollectionChanged += handler;
        this.subAgentDetailSubscriptions[subAgentViewModel] = handler;
    }

    private void OnSubAgentDetailContentsChanged(NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (AgentDetailDocumentItem item in e.NewItems)
            {
                if (!this.allDetailContents.Contains(item))
                {
                    this.allDetailContents.Add(item);
                }
            }
        }

        if (e.OldItems is not null)
        {
            foreach (AgentDetailDocumentItem item in e.OldItems)
            {
                this.allDetailContents.Remove(item);
            }
        }
    }

    private void RemoveSubAgentDetailContents(string agentId)
    {
        var subAgentViewModel = this.subAgentViewModels
            .FirstOrDefault(vm => string.Equals(vm.agentChat.AgentId, agentId, StringComparison.Ordinal));
        if (subAgentViewModel is null)
        {
            return;
        }

        if (this.subAgentDetailSubscriptions.TryGetValue(subAgentViewModel, out var handler))
        {
            ((INotifyCollectionChanged)subAgentViewModel.AllDetailContents).CollectionChanged -= handler;
            this.subAgentDetailSubscriptions.Remove(subAgentViewModel);
        }

        foreach (var item in subAgentViewModel.AllDetailContents)
        {
            this.allDetailContents.Remove(item);
        }
    }

    private void AddSubAgentSlotLazy(SubAgent stub)
    {
        // Start async lease acquisition and schedule UI update when complete
        var acquisitionTask = stub.AcquireLeaseAsync();
        acquisitionTask.ContinueWith(
            task =>
            {
                if (task.IsCompletedSuccessfully)
                {
                    var lease = task.Result;
                    // Hold the lease for the lifetime of the slot
                    lock (this.subAgentLeases)
                    {
                        this.subAgentLeases.Add(lease);
                    }
                    this.AddSubAgentSlotEager(stub, lease.AgentChat);
                }
                else if (task.IsFaulted)
                {
                    this.logger.LogError(task.Exception, "Failed to acquire lease for restored sub-agent {AgentId}", stub.SessionId.Value);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            this.foregroundScheduler);
    }

    /// <summary>
    /// Navigates to any agent in the loaded agent tree identified by its agent id or session id.
    /// Walks up to the root, then searches descendants, so navigating to a parent/ancestor id opens
    /// that agent's view. Falls through to <see cref="NavigateToSubAgent"/> for the resolved agent.
    /// </summary>
    public void NavigateToAgent(string agentId)
    {
        var root = this;
        while (root.ParentAgentViewModel is not null)
        {
            root = root.ParentAgentViewModel;
        }

        var target = root.FindInTreeById(agentId);
        var resolvedAgentId = target is not null ? target.agentChat.AgentId : agentId;
        root.NavigateToSubAgent(resolvedAgentId);
    }

    private AgentViewModel? FindInTreeById(string agentId)
    {
        if (string.Equals(this.agentChat.AgentId, agentId, StringComparison.Ordinal) ||
            string.Equals(this.agentChat.AgentSessionId, agentId, StringComparison.Ordinal))
        {
            return this;
        }

        foreach (var child in this.subAgentViewModels)
        {
            var found = child.FindInTreeById(agentId);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private void NavigateToSubAgent(string agentId)
    {
        // If the target is this agent itself, show the conversation view (navigate to self/root).
        if (string.Equals(agentId, this.agentChat.AgentId, StringComparison.Ordinal))
        {
            if (this.EditorItems.Count > 0)
            {
                this.SelectedEditorItem = this.EditorItems[0];
            }

            return;
        }

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
        private readonly SubAgentsContainerViewModel container;
        private readonly IList<AgentEditorNavigationItemViewModel> visibleChildren;

        public SubAgentsCollectionTransformer(
            IReadOnlyList<SubAgentSlotViewModel> source,
            IList<AgentEditorNavigationItemViewModel> allChildren,
            AgentEditorNavigationItemViewModel subAgentsNavItem)
            : base(source, allChildren)
        {
            this.subAgentsNavItem = subAgentsNavItem;
            this.container = (SubAgentsContainerViewModel)subAgentsNavItem.DetailContent;
            this.visibleChildren = subAgentsNavItem.Children;
            this.subAgentsNavItem.PropertyChanged += this.OnNavItemPropertyChanged;
            this.ApplyInitialTransform();
            this.RefreshVisibleChildren();
            this.UpdateSubAgentsLabel();
        }

        protected override AgentEditorNavigationItemViewModel Create(SubAgentSlotViewModel slot)
        {
            var subRoot = slot.SubAgentViewModel.EditorItems.FirstOrDefault();
            return new AgentEditorNavigationItemViewModel(
                $"sub-agent-{slot.AgentId}",
                slot.SubAgentViewModel.DisplayName,
                null,
                slot.SubAgentViewModel.Description,
                null,
                this.subAgentsNavItem.DetailContent,
                subRoot?.Children.ToArray() ?? [],
                runningSubAgent: slot.RunningSubAgent);
        }

        protected override void OnInsert(int index, AgentEditorNavigationItemViewModel target)
        {
            if (target.RunningSubAgent is AgentChat chat)
            {
                chat.CompletionStateChanged += (_, _) =>
                {
                    target.RefreshStatus();
                    this.container.NotifySubAgentUpdated();
                    this.RefreshVisibleChildren();
                };
            }

            this.RefreshVisibleChildren();
            this.UpdateSubAgentsLabel();
        }

        protected override void OnRemoveAt(int index, AgentEditorNavigationItemViewModel target)
        {
            this.RefreshVisibleChildren();
            this.UpdateSubAgentsLabel();
        }

        public override void Dispose()
        {
            this.subAgentsNavItem.PropertyChanged -= this.OnNavItemPropertyChanged;
            base.Dispose();
        }

        private void OnNavItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AgentEditorNavigationItemViewModel.HideCompletedAgents))
            {
                this.RefreshVisibleChildren();
            }
        }

        // Projects the full (unfiltered) set of sub-agent nav items in Target into
        // subAgentsNavItem.Children, excluding completed (Succeeded/Failed) items when
        // HideCompletedAgents is true. Preserves source order. See issue #1033.
        private void RefreshVisibleChildren()
        {
            var hide = this.subAgentsNavItem.HideCompletedAgents;

            var desired = new List<AgentEditorNavigationItemViewModel>();
            foreach (var item in this.Target)
            {
                if (!hide || !(item.IsSucceeded || item.IsFailed))
                {
                    desired.Add(item);
                }
            }

            for (int i = this.visibleChildren.Count - 1; i >= 0; i--)
            {
                if (!desired.Contains(this.visibleChildren[i]))
                {
                    this.visibleChildren.RemoveAt(i);
                }
            }

            for (int i = 0; i < desired.Count; i++)
            {
                if (i >= this.visibleChildren.Count || !ReferenceEquals(this.visibleChildren[i], desired[i]))
                {
                    var existing = this.visibleChildren.IndexOf(desired[i]);
                    if (existing >= 0)
                    {
                        this.visibleChildren.RemoveAt(existing);
                    }

                    this.visibleChildren.Insert(i, desired[i]);
                }
            }
        }

        private void UpdateSubAgentsLabel()
        {
            var count = this.Target.Count;
            this.subAgentsNavItem.Name = count > 0 ? $"Sub-agents ({count})" : "Sub-agents";
        }
    }
}
