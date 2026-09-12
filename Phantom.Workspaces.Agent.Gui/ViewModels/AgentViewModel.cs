using AgentSchema;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
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
    private readonly IAgentChat agentChat;
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
    private readonly ObservableCollection<AgentSessionModalViewModel> modalSource = [];
    private readonly Dictionary<AgentViewModel, NotifyCollectionChangedEventHandler> subAgentDetailSubscriptions = new();
    private readonly Dictionary<AgentViewModel, PropertyChangedEventHandler> subAgentModalSubscriptions = new();
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

    // #1451: Restore-settle tracking. Sub-agent lease-acquisition continuations registered while the
    // constructor seeds restored sub-agents are collected here so RestoreSettled can await them: the
    // running-state churn those continuations drive during rehydration must not be surfaced as a
    // user-initiated run by notification consumers.
    private readonly List<Task> restoreAcquisitionContinuations = [];
    private bool capturingRestoreContinuations;
    private Task? restoreSettled;

    // #1122: foregroundScheduler is a required constructor parameter. A silent
    // TaskScheduler.Default fallback caused sub-agent restore continuations to run on the
    // thread pool and mutate UI-bound collections off the UI thread, crashing the app.
    // Every construction site must supply a UI-thread scheduler (e.g. captured via
    // SynchronizationContextTaskScheduler.FromCurrent() on the UI thread) so that the
    // continuation in AddSubAgentSlotLazy performs its UI-affine mutations on the correct
    // thread. Tests that do not exercise UI-thread affinity may pass TaskScheduler.Default.
    /// <summary>
    /// #1485: named-initialiser construction for the common chat surface.
    /// </summary>
    public AgentViewModel(AgentViewModelOptions options)
        : this(
            (options ?? throw new ArgumentNullException(nameof(options))).AgentChat,
            options.DisplayName,
            options.Description,
            options.LoggerFactory,
            options.ForegroundScheduler,
            options.ParentAgentViewModel)
    {
    }

    internal AgentViewModel(IAgentChat agentChat, string displayName, string description, ObservableLoggerFactory loggerFactory, TaskScheduler foregroundScheduler, AgentViewModel? parentAgentViewModel = null)
    {
        ArgumentNullException.ThrowIfNull(agentChat);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        this.agentChat = agentChat;
        this.loggerFactory = loggerFactory;
        this.logger = loggerFactory.CreateLogger<AgentViewModel>();
        this.foregroundScheduler = foregroundScheduler ?? throw new ArgumentNullException(nameof(foregroundScheduler));
        if (foregroundScheduler is SynchronizationContextTaskScheduler synchronizationScheduler
            && SynchronizationContext.Current is { } currentContext
            && currentContext != synchronizationScheduler.SynchronizationContext)
        {
            throw new InvalidOperationException("AgentViewModel must be constructed on its foreground scheduler.");
        }
        this.agentSessionId = agentChat.Information.AgentSessionId;
        this.ParentAgentViewModel = parentAgentViewModel;
        this.ParentAgentDisplay = parentAgentViewModel?.agentChat is AgentChat parentLocalChat
            ? new RunningParentAgentDisplay(parentLocalChat)
            : null;
        this.DisplayName = displayName;
        this.Description = description;
        this.conversationDetail = new AgentChatConversationDetailViewModel(this);
        this.chatDetailsDetail = new AgentChatDetailsViewModel(this);
        this.toolsDetail = new AgentChatToolsDetailViewModel();
        this.subAgentsBrowserDetail = new SubAgentBrowserViewModel(agentChat.SubAgents);
        this.subAgentsContainerDetail = new SubAgentsContainerViewModel(this.subAgentsBrowserDetail);
        this.SubAgentDisplays = new ReadOnlyObservableCollection<IRunningSubAgentDisplay>(this.subAgentDisplayItems);
        this.Modals = new ReadOnlyObservableCollection<AgentSessionModalViewModel>(this.modalSource);
        this.InterruptCommand = new AsyncRelayCommand(
            _ => agentChat.InterruptAsync());
        this.ToggleReasoningVisibilityCommand = new RelayCommand(this.ToggleReasoningVisibility);
        this.RequestOpenLogWindowCommand = new RelayCommand(this.RequestOpenLogWindow);
        this.InputQueue = agentChat.Information.AcceptsUserInput
            ? new InputQueueViewModel(new InputQueueViewModelOptions
            {
                AgentChat = agentChat,
                ForegroundScheduler = foregroundScheduler,
            })
            : null;
        this.ToggleHoldAllQueuesCommand = new RelayCommand(() => this.InputQueue?.ToggleHoldAllQueuesCommand.Execute(null));
        this.HoldAllQueuesCommand = new RelayCommand(() => this.InputQueue?.HoldAllQueuesCommand.Execute(null));
        this.UnholdAllQueuesCommand = new RelayCommand(() => this.InputQueue?.UnholdAllQueuesCommand.Execute(null));
        this.EditorItems = [];

        this.NavigateToAgentHandler = this.NavigateToAgent;

        this.agentChat.InformationChanged += this.OnInformationChanged;
        this.agentChat.ToolsChanged += this.OnToolsChanged;
        this.agentChat.UsageChanged += this.OnUsageChanged;
        if (this.RunningItems is INotifyCollectionChanged runningItemsNotifications)
        {
            runningItemsNotifications.CollectionChanged += this.OnRunningItemsCollectionChanged;
        }
        ((INotifyCollectionChanged)agentChat.SubAgents).CollectionChanged += this.OnSubAgentsCollectionChanged;
        ((INotifyCollectionChanged)agentChat.Modals).CollectionChanged += this.OnLocalModalsChanged;

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
        // #1451: capture the restore-time sub-agent lease-acquisition continuations so RestoreSettled
        // can span them.
        this.capturingRestoreContinuations = true;
        foreach (var subAgent in agentChat.SubAgents)
        {
            this.AddSubAgentSlot(subAgent);
        }
        this.capturingRestoreContinuations = false;

        this.ApplyToolSnapshot(agentChat.GetToolSnapshot());
        this.RefreshModalProjection();
    }

    public string DisplayName { get; }

    public string Description { get; }

    /// <summary>
    /// Caller-supplied sub-agent name/id (issue #1151) sourced from <c>AgentChat.Name</c>. Empty
    /// for root agents and for sub-agents whose caller did not supply a name.
    /// </summary>
    public string Name => this.agentChat.Information.Name;

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

    public string ModelId => this.agentChat.Information.CurrentModelId ?? string.Empty;

    public long? TotalInputTokenCount => this.agentChat.Usage.TotalInputTokenCount;

    public long? TotalOutputTokenCount => this.agentChat.Usage.TotalOutputTokenCount;

    public long? TotalCacheReadTokenCount => this.agentChat.Usage.TotalCacheReadTokenCount;

    public long? TotalCacheWriteTokenCount => this.agentChat.Usage.TotalCacheWriteTokenCount;

    public long? TotalReasoningTokenCount => this.agentChat.Usage.TotalReasoningTokenCount;

    public double? TotalSessionCostUsd => this.agentChat.Usage.TotalSessionCostUsd;

    public string ModelApiType => this.ResolveAgentModel()?.ApiType ?? string.Empty;

    public string ModelConnectionType => this.ResolveAgentModel()?.Connection switch
    {
        null => "(none)",
        ApiKeyConnection => "API key",
        AnonymousConnection => "Anonymous",
        var connection => connection.GetType().Name,
    };

    public IAgentChat AgentChat => this.agentChat;

    /// <summary>
    /// The unresolved modal stack for this editor's own chat. Descendant stacks are rendered and
    /// gated by their own editors, while contributing only to the root aggregate state.
    /// </summary>
    public ReadOnlyObservableCollection<AgentSessionModalViewModel> Modals { get; }

    /// <summary>Compatibility alias for callers compiled against the initial common-chat surface.</summary>
    public ReadOnlyObservableCollection<AgentSessionModalViewModel> ModalProjection => this.Modals;

    internal AgentChat LocalAgentChat => (AgentChat)this.agentChat;

    /// <summary>Whether this agent accepts user input (false for hosted sub-agents).</summary>
    public bool AcceptsUserInput => this.agentChat.Information.AcceptsUserInput;

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

    public bool IsInputGated => this.agentChat.Modals.Count > 0;

    public bool HasModalsNeedingInput =>
        this.Modals.Count > 0 || this.subAgentViewModels.Any(agent => agent.HasModalsNeedingInput);

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
    public global::Dock.Model.Controls.IRootDock DetailLayout => this.detailDockFactory.Layout;

    /// <summary>The cached detail document currently active in the detail dock, or null.</summary>
    public AgentDetailDocument? SelectedDetailDocument => this.detailDockFactory.GetDocument(this.selectedDetailItem);

    /// <summary>Test/host seam: the factory that owns the detail dock and its document registry.</summary>
    internal AgentDetailDockFactory DetailDockFactory => this.detailDockFactory;

    public bool IsChatRunning => this.RunningItems.Count > 0;

    /// <summary>
    /// #1451: Completes once this agent has finished rehydrating from persistence — both persisted
    /// history population (<see cref="HistoryPopulated"/>) AND any restore-time sub-agent
    /// lease-acquisition continuations captured while the constructor seeds restored sub-agents.
    /// Consumers gate resume-time notification suppression on this signal so the running-state churn
    /// produced while a session rehydrates (including sub-agent/lease acquisition continuations that
    /// settle asynchronously after the view model is wired up) is never surfaced as a user-initiated
    /// run. The task is created lazily on first access so a caller-controlled
    /// <see cref="SetHistoryPopulatedForTest"/> gate is honoured.
    /// </summary>
    public Task RestoreSettled => this.restoreSettled ??= this.ComputeRestoreSettledAsync();

    private async Task ComputeRestoreSettledAsync()
    {
        try
        {
            await this.HistoryPopulated.ConfigureAwait(true);
        }
        catch
        {
            // History-population failures surface elsewhere; the restore window still closes so the
            // tab does not stay wedged in its restoring state.
        }

        Task[] continuations;
        lock (this.restoreAcquisitionContinuations)
        {
            continuations = this.restoreAcquisitionContinuations.ToArray();
        }

        if (continuations.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(continuations).ConfigureAwait(true);
        }
        catch
        {
            // Individual acquisition failures are already logged in AddSubAgentSlotLazy; settle
            // regardless so notifications resume once rehydration has fully drained.
        }
    }

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

            // Fix #1112: only the "Sub-agents (N)" group node still uses the shared container as
            // its DetailContent (for the browser card). Individual sub-agent nav items now carry
            // their own ConversationDetail, so they resolve to a distinct AgentDetailDocumentItem
            // via the ReferenceEquals scan below and no longer need ShowSubAgent slot toggling.
            if (value is not null && ReferenceEquals(value.DetailContent, this.subAgentsContainerDetail))
            {
                this.subAgentsContainerDetail.ShowBrowser();
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

    public Task RespondToModalAsync(string modalId, JsonElement response, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modalId);
        if (!this.agentChat.Modals.Any(modal => string.Equals(modal.Id, modalId, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Unknown modal id '{modalId}'.", nameof(modalId));
        }

        return this.agentChat.RespondToModalAsync(modalId, response, ct);
    }

    public void DismissModal(string modalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modalId);
        if (!this.agentChat.Modals.Any(modal => string.Equals(modal.Id, modalId, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Unknown modal id '{modalId}'.", nameof(modalId));
        }

        if (this.agentChat is AgentChat localAgentChat)
        {
            localAgentChat.PublishModalDismiss(modalId);
        }
    }

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
            await this.EnqueueUserMessageAsync(text).ConfigureAwait(false);
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

        // Transient slash-command results (e.g. /model) are shown in the visible chat
        // transcript as non-persisted diagnostic notes so the user gets feedback. The note
        // is added to in-memory History only and is never written to the store, so it does
        // not reappear after a reload (issue #1396).
        if (result.IsTransient)
        {
            this.agentChat.EnqueueTransientDiagnostic(result.StatusMessage);
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

    private void OnInformationChanged(object? sender, EventArgs e)
    {
        this.AgentSessionId = this.agentChat.Information.AgentSessionId;
        this.OnModelChanged(sender, e);
    }

    private Task<AgentInputQueueCommandResult> EnqueueUserMessageAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        var queues = this.agentChat.InputQueues;
        return queues.EnqueueAsync(
            new EnqueueAgentInputRequest
            {
                TargetQueueId = queues.DefaultQueue.Snapshot.QueueId,
                Messages = [new ChatMessage(ChatRole.User, text)],
                CommandId = Guid.NewGuid(),
                ExpectedRevision = queues.Snapshot.Revision,
            },
            cancellationToken);
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
        foreach (var (subAgentViewModel, handler) in this.subAgentModalSubscriptions)
        {
            subAgentViewModel.PropertyChanged -= handler;
        }
        this.subAgentModalSubscriptions.Clear();
        this.InputQueue?.Dispose();
        this.conversationDetail.Dispose();
        this.subAgentsBrowserDetail.Dispose();
        ((INotifyCollectionChanged)this.agentChat.SubAgents).CollectionChanged -= this.OnSubAgentsCollectionChanged;
        ((INotifyCollectionChanged)this.agentChat.Modals).CollectionChanged -= this.OnLocalModalsChanged;
        this.agentChat.InformationChanged -= this.OnInformationChanged;
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
        var agentDefinition = this.agentChat.Information.AgentDefinition;
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
        var subAgentViewModel = new AgentViewModel(new AgentViewModelOptions
        {
            AgentChat = subAgentChat,
            DisplayName = subAgent.DisplayName,
            Description = subAgent.Description,
            LoggerFactory = this.loggerFactory,
            ForegroundScheduler = this.foregroundScheduler,
            ParentAgentViewModel = this,
        });
        // Delegate the sub-agent's navigation handler to this parent so ancestor navigation works
        // (issue #1046): the parent can resolve its own children, and if the target is above this
        // agent it falls through to ancestor resolution logic in NavigateToSubAgent.
        subAgentViewModel.NavigateToAgentHandler = this.NavigateToAgentHandler;
        this.subAgentViewModels.Add(subAgentViewModel);
        PropertyChangedEventHandler modalHandler = (_, e) =>
        {
            if (e.PropertyName == nameof(HasModalsNeedingInput))
            {
                this.RaisePropertyChanged(nameof(this.HasModalsNeedingInput));
            }
        };
        subAgentViewModel.PropertyChanged += modalHandler;
        this.subAgentModalSubscriptions[subAgentViewModel] = modalHandler;
        // Recursively aggregate the sub-agent's flat detail-content collection into this agent's
        // collection so every sub-agent node (and its descendants) has a first-class cached document
        // in the root dock (issue #1035). The sub-agent's collection already includes its own
        // sub-agents, so arbitrary nesting depth is handled without special-casing.
        this.AppendSubAgentDetailContents(subAgentViewModel);
        // Use the AgentChat's AgentId, not the stub's AgentId (which may be the session ID for lazy stubs)
        this.subAgentsContainerDetail.AddSlot(subAgentChat.AgentId, subAgentViewModel, subAgentChat);
        this.RefreshModalProjection();
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
            .FirstOrDefault(vm => string.Equals(vm.agentChat.Information.AgentId, agentId, StringComparison.Ordinal));
        if (subAgentViewModel is null)
        {
            return;
        }

        if (this.subAgentDetailSubscriptions.TryGetValue(subAgentViewModel, out var handler))
        {
            ((INotifyCollectionChanged)subAgentViewModel.AllDetailContents).CollectionChanged -= handler;
            this.subAgentDetailSubscriptions.Remove(subAgentViewModel);
        }
        if (this.subAgentModalSubscriptions.TryGetValue(subAgentViewModel, out var modalHandler))
        {
            subAgentViewModel.PropertyChanged -= modalHandler;
            this.subAgentModalSubscriptions.Remove(subAgentViewModel);
        }

        foreach (var item in subAgentViewModel.AllDetailContents)
        {
            this.allDetailContents.Remove(item);
        }
        this.subAgentViewModels.Remove(subAgentViewModel);
        this.subAgentsContainerDetail.RemoveSlot(agentId);
        var display = this.subAgentDisplayItems.FirstOrDefault(
            item => string.Equals(item.AgentId, agentId, StringComparison.Ordinal));
        if (display is not null)
        {
            this.subAgentDisplayItems.Remove(display);
            if (display is IDisposable disposable)
                disposable.Dispose();
        }
        this.RefreshModalProjection();
        _ = this.DisposeRemovedSubAgentViewModelAsync(subAgentViewModel);
    }

    private async Task DisposeRemovedSubAgentViewModelAsync(AgentViewModel subAgentViewModel)
    {
        try
        {
            await subAgentViewModel.DisposeViewResourcesAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            this.logger.LogError(
                exception,
                "Failed to dispose removed sub-agent view resources for {AgentId}",
                subAgentViewModel.AgentChat.Information.AgentId);
        }
    }

    private void AddSubAgentSlotLazy(SubAgent stub)
    {
        // #1122: The continuation body performs UI-affine mutations (allDetailContents,
        // subAgentDisplayItems, Dock updates via AgentDetailDockFactory) and MUST run on the
        // UI-thread foregroundScheduler. The scheduler is now a required constructor
        // parameter so the value is guaranteed to be a real UI-thread scheduler in production.
        // We also wrap the success branch in try/catch so any exception thrown by the UI-thread
        // work is observed and logged rather than surfacing as an unobserved-task exception on
        // the finalizer thread (which previously crashed the process).
        var acquisitionTask = stub.AcquireLeaseAsync();
        var acquisitionContinuation = acquisitionTask.ContinueWith(
            async task =>
            {
                if (task.IsCompletedSuccessfully)
                {
                    var lease = await task;
                    // Hold the lease for the lifetime of the slot
                    lock (this.subAgentLeases)
                    {
                        this.subAgentLeases.Add(lease);
                    }
                    try
                    {
                        this.AddSubAgentSlotEager(stub, lease.LocalAgentChat);
                    }
                    catch (Exception ex)
                    {
                        this.logger.LogError(ex, "Failed to add restored sub-agent slot {AgentId}", stub.SessionId.Value);
                    }
                }
                else if (task.IsFaulted)
                {
                    this.logger.LogError(task.Exception, "Failed to acquire lease for restored sub-agent {AgentId}", stub.SessionId.Value);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            this.foregroundScheduler).Unwrap();

        // #1451: while the constructor seeds restored sub-agents, record each acquisition
        // continuation so RestoreSettled awaits the UI-affine running-state mutations they perform.
        if (this.capturingRestoreContinuations)
        {
            lock (this.restoreAcquisitionContinuations)
            {
                this.restoreAcquisitionContinuations.Add(acquisitionContinuation);
            }
        }
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
        var resolvedAgentId = target is not null ? target.agentChat.Information.AgentId : agentId;
        root.NavigateToSubAgent(resolvedAgentId);
    }

    private AgentViewModel? FindInTreeById(string agentId)
    {
        // Fix #1152: An empty/whitespace id must never match. Two unrelated sub-agents whose
        // agentId is empty (e.g. seeded before the AgentChat.agentId fallback landed, or in
        // partially initialized states) would otherwise silently collide here.
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return null;
        }

        if (string.Equals(this.agentChat.Information.AgentId, agentId, StringComparison.Ordinal) ||
            string.Equals(this.agentChat.Information.AgentSessionId, agentId, StringComparison.Ordinal))
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
        // Fix #1152: Guard against empty/whitespace ids before the string.Equals check below,
        // which would otherwise treat "empty == this agent's empty AgentId" as "navigate to self"
        // and dismiss the click silently. A blank id has no meaningful target — do nothing.
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return;
        }

        // If the target is this agent itself, show the conversation view (navigate to self/root).
        if (string.Equals(agentId, this.agentChat.Information.AgentId, StringComparison.Ordinal))
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

        // Fix #1134: resolve against the FULL (unfiltered) set so completed (Succeeded/Failed)
        // sub-agents — which HideCompletedAgents removes from subAgentsGroup.Children — are still
        // found and their own AgentDetailDocumentItem (its HTML view) is activated via the
        // SelectedEditorItem setter's ReferenceEquals scan. Using the filtered
        // subAgentsGroup.Children caused completed sub-agents to fall back to the group node,
        // which shows the shared browser card instead of the sub-agent's own transcript.
        var childItem = this.subAgentAllChildren.FirstOrDefault(c =>
            c.Id == $"sub-agent-{agentId}");

        subAgentsGroup.IsExpanded = true;
        this.SelectedEditorItem = childItem ?? subAgentsGroup;

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

    private void OnModelChanged(object? sender, EventArgs eventArgs)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            this.RaiseModelPropertiesChanged();
            return;
        }

        Dispatcher.UIThread.Post(this.RaiseModelPropertiesChanged);
    }

    private void RaiseModelPropertiesChanged()
    {
        this.RaisePropertyChanged(nameof(this.ModelId));
        this.RaisePropertyChanged(nameof(this.ModelProvider));
    }

    private void RaiseUsagePropertiesChanged()
    {
        this.RaisePropertyChanged(nameof(this.TotalInputTokenCount));
        this.RaisePropertyChanged(nameof(this.TotalOutputTokenCount));
        this.RaisePropertyChanged(nameof(this.TotalCacheReadTokenCount));
        this.RaisePropertyChanged(nameof(this.TotalCacheWriteTokenCount));
        this.RaisePropertyChanged(nameof(this.TotalReasoningTokenCount));
        this.RaisePropertyChanged(nameof(this.TotalSessionCostUsd));
    }

    private void OnLocalModalsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => this.RefreshModalProjection();

    private void RefreshModalProjection()
    {
        var existing = this.modalSource.ToDictionary(modal => modal.Id, StringComparer.Ordinal);
        var desired = new List<AgentSessionModalViewModel>(this.agentChat.Modals.Count);
        foreach (var modal in this.agentChat.Modals)
        {
            if (existing.TryGetValue(modal.Id, out var current)
                && string.Equals(current.Title, modal.Title, StringComparison.Ordinal)
                && string.Equals(current.Body, modal.Body, StringComparison.Ordinal)
                && ModalContentEquals(current.Content, modal.Content))
            {
                desired.Add(current);
            }
            else
            {
                desired.Add(new AgentSessionModalViewModel(this, modal));
            }
        }

        for (var index = this.modalSource.Count - 1; index >= 0; index--)
        {
            if (!desired.Contains(this.modalSource[index]))
            {
                this.modalSource.RemoveAt(index);
            }
        }
        for (var index = 0; index < desired.Count; index++)
        {
            var currentIndex = this.modalSource.IndexOf(desired[index]);
            if (currentIndex < 0)
            {
                this.modalSource.Insert(index, desired[index]);
            }
            else if (currentIndex != index)
            {
                this.modalSource.Move(currentIndex, index);
            }
        }

        this.RaisePropertyChanged(nameof(this.IsInputGated));
        this.RaisePropertyChanged(nameof(this.HasModalsNeedingInput));
    }

    private static bool ModalContentEquals(
        AgentChatModalContent left,
        AgentChatModalContent right)
        => (left, right) switch
        {
            (MultipleChoiceModalContent leftChoices, MultipleChoiceModalContent rightChoices) =>
                leftChoices.AllowsMultiple == rightChoices.AllowsMultiple
                && leftChoices.Options.Count == rightChoices.Options.Count
                && leftChoices.Options.Zip(rightChoices.Options).All(pair =>
                    string.Equals(
                        pair.First.GetRawText(),
                        pair.Second.GetRawText(),
                        StringComparison.Ordinal)),
            _ => Equals(left, right),
        };

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
            // Fix #1112: each sub-agent nav item's DetailContent is its OWN ConversationDetail (not
            // the shared subAgentsContainerDetail). That way SelectedEditorItem's ReferenceEquals
            // scan over AllDetailContents finds the sub-agent's own AgentDetailDocumentItem (added
            // via AppendSubAgentDetailContents), so SetActiveDetail activates a distinct Document
            // per sub-agent. The DocumentDock only realises the active Document's content, so at
            // most one AgentChatOutputControl / native WebView2 surface is materialised at a time —
            // eliminating the airspace overlap where every sub-agent showed the same transcript.
            return new AgentEditorNavigationItemViewModel(
                $"sub-agent-{slot.AgentId}",
                slot.SubAgentViewModel.DisplayName,
                null,
                slot.SubAgentViewModel.Description,
                null,
                slot.SubAgentViewModel.ConversationDetail,
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
        // HideCompletedAgents is true. See issue #1033.
        //
        // Fix #1153: Applies a composite ordering to the visible children BEFORE reconciliation:
        //   1. Running/idle items first, completed (Succeeded/Failed) items last.
        //   2. Within each group, most-recently-updated first (descending LastUpdatedAt), so
        //      the item the operator is actively watching stays near the top and completed
        //      history sinks to the bottom.
        // The reconciliation loop below moves items to their new positions in-place so
        // Avalonia's ItemsControl sees ordinary Move/Add/Remove events, not a full reset.
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

            desired = desired
                .OrderBy(item => (item.IsSucceeded || item.IsFailed) ? 1 : 0)
                .ThenByDescending(item => item.LastUpdatedAt ?? DateTime.MinValue)
                .ToList();

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
