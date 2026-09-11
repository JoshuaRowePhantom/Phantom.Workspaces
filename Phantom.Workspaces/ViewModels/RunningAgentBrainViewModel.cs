using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.Navigation;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Backs the brain-icon toolbar button and popup that lists every active agent session.
/// </summary>
internal sealed class RunningAgentBrainViewModel : ViewModelBase, IDisposable
{
    private readonly IRunningAgentChatTable table;
    private readonly Func<IEnumerable<AgentTabInfo>> getAllAgentTabs;
    private readonly ITabNavigator navigator;
    private readonly Action<Action> dispatch;
    private readonly TimeProvider timeProvider;

    private bool isAnyRunning;
    private bool isAnyAgentPulsating;
    private bool isOpen;
    private bool _disposed;

    // Per-row subscriptions: sessionKey → (tab, tabHandler, agentHandler)
    private readonly List<(string sessionKey, AgentSessionWorkspaceTabViewModel tab,
        PropertyChangedEventHandler tabHandler,
        PropertyChangedEventHandler? agentHandler)> rowSubscriptions = [];

    // Row IsThinking subscriptions: sessionKey → (row, handler). Kept in sync with `Rows` so the
    // aggregate `IsAnyAgentPulsating` (issue #1305) recomputes whenever any row's IsThinking flips,
    // including on row replacement (tab ↔ fallback) and row removal.
    private readonly Dictionary<string, (RunningAgentRowViewModel Row, PropertyChangedEventHandler Handler)> rowThinkingSubscriptions
        = new(StringComparer.Ordinal);

    // The running-table entry is the authoritative source for remote retention and viewer
    // metadata. Keep this independent of the optional open-tab subscriptions so fallback rows
    // receive the same updates.
    private readonly Dictionary<string, (RunningAgentChatWithEntityInfo Session, PropertyChangedEventHandler Handler)> sessionMetadataSubscriptions
        = new(StringComparer.Ordinal);

    // History subscriptions: sessionKey → (history, handler)
    private readonly Dictionary<string, (AgentChatHistoryCollection History, NotifyCollectionChangedEventHandler Handler)> historySubscriptions
        = new(StringComparer.Ordinal);

    public RunningAgentBrainViewModel(
        IRunningAgentChatTable table,
        Func<IEnumerable<AgentTabInfo>> getAllAgentTabs,
        ITabNavigator navigator,
        Action<Action> dispatch,
        TimeProvider? timeProvider = null)
    {
        this.table = table;
        this.getAllAgentTabs = getAllAgentTabs;
        this.navigator = navigator;
        this.dispatch = dispatch;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.ToggleOpenCommand = new RelayCommand(_ => this.ToggleOpen());
        this.table.RunningSessions.CollectionChanged += this.OnSessionsChanged;
        this.Refresh();
    }

    /// <summary>True while at least one agent session is registered in the running-agent table.</summary>
    public bool IsAnyRunning
    {
        get => this.isAnyRunning;
        private set => this.SetProperty(ref this.isAnyRunning, value);
    }

    /// <summary>
    /// True when at least one non-sub-agent row is currently in the "pulsating" (thinking / actively
    /// working) state. Drives the toolbar brain's animation so it only pulsates when at least one
    /// running agent is itself pulsating (issue #1305).
    /// </summary>
    public bool IsAnyAgentPulsating
    {
        get => this.isAnyAgentPulsating;
        private set => this.SetProperty(ref this.isAnyAgentPulsating, value);
    }

    /// <summary>Whether the popup is open.</summary>
    public bool IsOpen
    {
        get => this.isOpen;
        set => this.SetProperty(ref this.isOpen, value);
    }

    public ICommand ToggleOpenCommand { get; }

    public ObservableCollection<RunningAgentRowViewModel> Rows { get; } = [];

    public bool HasRows => this.Rows.Count > 0;

    private void ToggleOpen() => this.IsOpen = !this.IsOpen;

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        this.dispatch(this.Refresh);

    private void SubscribeRowThinking(RunningAgentRowViewModel row)
    {
        PropertyChangedEventHandler handler = (_, e) =>
        {
            if (e.PropertyName == nameof(RunningAgentRowViewModel.IsThinking))
            {
                this.RecomputeIsAnyAgentPulsating();
            }
        };
        row.PropertyChanged += handler;
        this.rowThinkingSubscriptions[row.SessionKey] = (row, handler);
    }

    private void UnsubscribeRowThinking(string sessionKey)
    {
        if (this.rowThinkingSubscriptions.TryGetValue(sessionKey, out var sub))
        {
            sub.Row.PropertyChanged -= sub.Handler;
            this.rowThinkingSubscriptions.Remove(sessionKey);
        }
    }

    private void RecomputeIsAnyAgentPulsating()
    {
        this.IsAnyAgentPulsating = this.Rows.Any(r => r.IsThinking);
    }

    /// <summary>
    /// Rebuilds <see cref="Rows"/> from <see cref="IRunningAgentChatTable.RunningSessions"/>,
    /// pairing each session with its open tab (if any) or creating a fallback row.
    /// Also updates <see cref="IsAnyRunning"/>.
    /// </summary>
    public void Refresh()
    {
        if (this._disposed) return;
        this.IsAnyRunning = this.table.RunningSessions.Any(s => !s.IsSubAgent);

        // Build lookup: agentSessionId → AgentTabInfo (only Ready tabs with a known session ID)
        var tabsBySessionId = new Dictionary<string, AgentTabInfo>(StringComparer.Ordinal);
        foreach (var tabInfo in this.getAllAgentTabs())
        {
            if (tabInfo.Tab.AgentSessionId is { } sid && !tabsBySessionId.ContainsKey(sid))
            {
                tabsBySessionId[sid] = tabInfo;
            }
        }

        var currentSessionKeys = new HashSet<string>(
            this.table.RunningSessions
                .Where(s => !s.IsSubAgent)
                .Select(s => s.SessionId.Value),
            StringComparer.Ordinal);

        // Remove rows for sessions no longer in the table
        var keysToRemove = this.Rows
            .Where(r => !currentSessionKeys.Contains(r.SessionKey))
            .Select(r => r.SessionKey)
            .ToList();

        foreach (var key in keysToRemove)
        {
            var rowIndex = this.IndexOfRow(key);
            if (rowIndex >= 0)
            {
                this.Rows.RemoveAt(rowIndex);
            }

            this.UnsubscribeRowThinking(key);
            this.UnsubscribeRow(key);
            this.UnsubscribeSessionMetadata(key);
        }

        // Add or update a row for each active session
        foreach (var session in this.table.RunningSessions.ToList())
        {
            // Issue #1205 Fix 2 (defensive): sub-agents must never appear in the running-agents
            // flyout. Fix 1 blocks the leak at the factory; this filter guarantees a stray
            // sub-agent registration cannot render as a "No Open Tab" row.
            if (session.IsSubAgent)
            {
                continue;
            }

            var sessionKey = session.SessionId.Value;
            var hasTab = tabsBySessionId.TryGetValue(sessionKey, out var tabInfo);
            var existing = this.Rows.FirstOrDefault(r =>
                string.Equals(r.SessionKey, sessionKey, StringComparison.Ordinal));

            if (existing is null)
            {
                var row = hasTab
                    ? this.CreateTabRow(session, tabInfo)
                    : this.CreateFallbackRow(session);
                this.Rows.Add(row);
                this.SubscribeRowThinking(row);
                this.SubscribeSessionMetadata(session);
                if (hasTab)
                {
                    this.SubscribeRow(sessionKey, tabInfo.Tab);
                }
            }
            else if (hasTab && !existing.HasOpenTab)
            {
                // Fallback → tab row: tab appeared for this session
                this.UnsubscribeRowThinking(sessionKey);
                this.UnsubscribeRow(sessionKey);
                var rowIndex = this.IndexOfRow(sessionKey);
                var replacement = this.CreateTabRow(session, tabInfo);
                this.Rows[rowIndex] = replacement;
                existing = replacement;
                this.SubscribeRowThinking(replacement);
                this.SubscribeRow(sessionKey, tabInfo.Tab);
            }
            else if (!hasTab && existing.HasOpenTab)
            {
                // Tab row → fallback: tab disappeared but session is still running
                this.UnsubscribeRowThinking(sessionKey);
                this.UnsubscribeRow(sessionKey);
                var rowIndex = this.IndexOfRow(sessionKey);
                var replacement = this.CreateFallbackRow(session);
                this.Rows[rowIndex] = replacement;
                existing = replacement;
                this.SubscribeRowThinking(replacement);
            }
            else if (hasTab)
            {
                existing.IsThinking = tabInfo.Tab.Agent?.IsChatRunning ?? false;
            }

            var activeTurn = session.IsInterruptible
                || (hasTab && tabInfo.Tab.Agent?.IsChatRunning == true);
            existing ??= this.Rows.FirstOrDefault(row =>
                string.Equals(row.SessionKey, sessionKey, StringComparison.Ordinal));
            existing?.UpdateRuntimeMetadata(session, activeTurn);
        }

        this.RaisePropertyChanged(nameof(this.HasRows));
        this.ResortRows();
        this.RecomputeIsAnyAgentPulsating();
    }

    private RunningAgentRowViewModel CreateTabRow(RunningAgentChatWithEntityInfo session, AgentTabInfo tabInfo)
    {
        var sessionKey = session.SessionId.Value;
        var capturedTabId = tabInfo.Tab.Id;
        var capturedPaneId = tabInfo.PaneId;

        ICommand activateCmd = new RelayCommand(async _ =>
        {
            this.IsOpen = false;
            await this.navigator.NavigateAsync(
                new NavigationTarget
                {
                    Path = new UiPath(capturedPaneId, capturedTabId),
                    AgentSessionKey = sessionKey,
                },
                new NavigationOptions { OpenEntityIfNoTab = true, FocusWindow = true });
        });

        return this.CreateRuntimeRow(
            session,
            tabInfo.PaneTitle,
            tabInfo.Tab.Title,
            entityName: null,
            hasOpenTab: true,
            isThinking: session.IsInterruptible || tabInfo.Tab.Agent?.IsChatRunning == true,
            activateCommand: activateCmd);
    }

    private RunningAgentRowViewModel CreateFallbackRow(RunningAgentChatWithEntityInfo session)
    {
        var capturedSessionKey = session.SessionId.Value;

        ICommand activateCmd = new RelayCommand(async _ =>
        {
            this.IsOpen = false;
            await this.navigator.NavigateAsync(
                new NavigationTarget { AgentSessionKey = capturedSessionKey },
                new NavigationOptions { OpenEntityIfNoTab = true, FocusWindow = true });
        });

        return this.CreateRuntimeRow(
            session,
            workspacePaneTitle: null,
            tabTitle: null,
            entityName: session.EntityName,
            hasOpenTab: false,
            isThinking: session.IsInterruptible,
            activateCommand: activateCmd);
    }

    private RunningAgentRowViewModel CreateRuntimeRow(
        RunningAgentChatWithEntityInfo session,
        string? workspacePaneTitle,
        string? tabTitle,
        string? entityName,
        bool hasOpenTab,
        bool isThinking,
        ICommand activateCommand)
    {
        return new RunningAgentRowViewModel(
            session,
            workspacePaneTitle,
            tabTitle,
            entityName,
            hasOpenTab,
            isThinking,
            activateCommand,
            async ct =>
            {
                await using var lease = await session.AcquireLeaseAsync(ct).ConfigureAwait(false);
                if (lease.AgentChat.RunningItems.Count > 0)
                {
                    if (lease.AgentChat is IAsyncInterruptibleAgentChat remoteChat)
                        await remoteChat.InterruptAsync(ct).ConfigureAwait(false);
                    else
                        lease.AgentChat.Interrupt();
                }
            },
            async ct =>
            {
                var terminated = await this.table.TerminateAsync(session.SessionId, ct).ConfigureAwait(false);
                if (!terminated)
                {
                    throw new InvalidOperationException("The agent session is no longer running.");
                }
            },
            (value, ct) => this.table.SetContinueInBackgroundAsync(session.SessionId, value, ct),
            this.timeProvider);
    }

    private void SubscribeSessionMetadata(RunningAgentChatWithEntityInfo session)
    {
        var key = session.SessionId.Value;
        if (this.sessionMetadataSubscriptions.ContainsKey(key))
        {
            return;
        }

        PropertyChangedEventHandler handler = (_, _) => this.dispatch(() =>
        {
            if (this._disposed)
            {
                return;
            }

            var row = this.Rows.FirstOrDefault(candidate =>
                string.Equals(candidate.SessionKey, key, StringComparison.Ordinal));
            if (row is not null)
            {
                var active = this.getAllAgentTabs().FirstOrDefault(tab =>
                    string.Equals(tab.Tab.AgentSessionId, key, StringComparison.Ordinal));
                row.UpdateRuntimeMetadata(
                    session,
                    session.IsInterruptible
                        || (active is { Tab: not null } && active.Tab.Agent?.IsChatRunning == true));
            }
        });
        session.PropertyChanged += handler;
        this.sessionMetadataSubscriptions[key] = (session, handler);
    }

    private void UnsubscribeSessionMetadata(string key)
    {
        if (this.sessionMetadataSubscriptions.Remove(key, out var subscription))
        {
            subscription.Session.PropertyChanged -= subscription.Handler;
        }
    }

    /// <summary>
    /// Sorts <see cref="Rows"/> in place by <see cref="RunningAgentRowViewModel.LastActivityAt"/> descending
    /// using <c>Move</c> operations to preserve item identity and avoid UI flicker.
    /// </summary>
    internal void ResortRows()
    {
        var sorted = this.Rows.OrderByDescending(r => r.LastActivityAt).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var current = this.Rows.IndexOf(sorted[i]);
            if (current != i)
            {
                this.Rows.Move(current, i);
            }
        }
    }

    private void SubscribeHistory(string sessionKey, AgentViewModel agent)
    {
        var history = agent.AgentChat.History;
        NotifyCollectionChangedEventHandler handler = (_, _) =>
        {
            var row = this.Rows.FirstOrDefault(r =>
                string.Equals(r.SessionKey, sessionKey, StringComparison.Ordinal));
            if (row is not null)
            {
                row.UpdateLastActivityAt(this.timeProvider.GetUtcNow().UtcDateTime);
                this.ResortRows();
            }
        };
        ((INotifyCollectionChanged)history).CollectionChanged += handler;
        this.historySubscriptions[sessionKey] = (history, handler);
    }

    private void UnsubscribeHistory(string sessionKey)
    {
        if (this.historySubscriptions.TryGetValue(sessionKey, out var sub))
        {
            ((INotifyCollectionChanged)sub.History).CollectionChanged -= sub.Handler;
            this.historySubscriptions.Remove(sessionKey);
        }
    }

    private void SubscribeRow(string sessionKey, AgentSessionWorkspaceTabViewModel tab)
    {
        PropertyChangedEventHandler? agentHandler = null;

        if (tab.Agent is { } agent)
        {
            agentHandler = CreateAgentHandler(sessionKey);
            agent.PropertyChanged += agentHandler;
            this.SubscribeHistory(sessionKey, agent);
        }

        PropertyChangedEventHandler tabHandler = (_, e) =>
        {
            if (e.PropertyName == nameof(AgentSessionWorkspaceTabViewModel.Agent))
            {
                this.UnsubscribeAgentHandler(sessionKey);
                this.UnsubscribeHistory(sessionKey);
                if (tab.Agent is { } newAgent)
                {
                    this.UpdateRowThinking(sessionKey, newAgent.IsChatRunning);
                    var newAgentHandler = this.CreateAgentHandler(sessionKey);
                    newAgent.PropertyChanged += newAgentHandler;
                    this.UpdateRowAgentHandler(sessionKey, newAgentHandler);
                    this.SubscribeHistory(sessionKey, newAgent);
                }
            }
        };

        tab.PropertyChanged += tabHandler;
        this.rowSubscriptions.Add((sessionKey, tab, tabHandler, agentHandler));
    }

    private PropertyChangedEventHandler CreateAgentHandler(string sessionKey) =>
        (_, e) =>
        {
            if (e.PropertyName != nameof(AgentViewModel.IsChatRunning))
            {
                return;
            }

            // AgentViewModel.IsChatRunning can be raised from the AgentChat process-loop
            // continuation (a non-UI context). Marshal onto the UI thread via dispatch before
            // touching the UI-owned Rows / getAllAgentTabs() collections, so the handler never
            // observes a torn/transient open-time state (issue #1037).
            this.dispatch(() =>
            {
                if (this._disposed)
                {
                    return;
                }

                var row = this.Rows.FirstOrDefault(r =>
                    string.Equals(r.SessionKey, sessionKey, StringComparison.Ordinal));

                // FirstOrDefault over the AgentTabInfo struct sequence returns default(AgentTabInfo)
                // (Tab == null) when no open tab matches — a legitimate transient state during the
                // open-session transition. The `is { Tab: not null }` property pattern correctly
                // treats that default as "no match" (a struct is never null, so a bare `is { }`
                // guard would wrongly succeed and dereference a null Tab), making this a safe no-op
                // instead of an NRE (issue #1037).
                if (row is not null && this.getAllAgentTabs().FirstOrDefault(
                        t => string.Equals(t.Tab.AgentSessionId, sessionKey, StringComparison.Ordinal)) is { Tab: not null } info)
                {
                    var session = this.table.RunningSessions.FirstOrDefault(candidate =>
                        string.Equals(candidate.SessionId.Value, sessionKey, StringComparison.Ordinal));
                    if (session is not null)
                    {
                        row.UpdateRuntimeMetadata(
                            session,
                            session.IsInterruptible || info.Tab.Agent?.IsChatRunning == true);
                    }
                }
            });
        };

    private void UnsubscribeRow(string sessionKey)
    {
        for (var i = this.rowSubscriptions.Count - 1; i >= 0; i--)
        {
            var (key, tab, tabHandler, agentHandler) = this.rowSubscriptions[i];
            if (!string.Equals(key, sessionKey, StringComparison.Ordinal))
            {
                continue;
            }

            tab.PropertyChanged -= tabHandler;
            if (agentHandler is not null && tab.Agent is { } agent)
            {
                agent.PropertyChanged -= agentHandler;
            }

            this.rowSubscriptions.RemoveAt(i);
        }

        this.UnsubscribeHistory(sessionKey);
    }

    private void UnsubscribeAgentHandler(string sessionKey)
    {
        for (var i = 0; i < this.rowSubscriptions.Count; i++)
        {
            var (key, tab, tabHandler, agentHandler) = this.rowSubscriptions[i];
            if (!string.Equals(key, sessionKey, StringComparison.Ordinal))
            {
                continue;
            }

            if (agentHandler is not null && tab.Agent is { } agent)
            {
                agent.PropertyChanged -= agentHandler;
            }

            this.rowSubscriptions[i] = (key, tab, tabHandler, null);
            break;
        }
    }

    private void UpdateRowAgentHandler(string sessionKey, PropertyChangedEventHandler newAgentHandler)
    {
        for (var i = 0; i < this.rowSubscriptions.Count; i++)
        {
            var (key, tab, tabHandler, _) = this.rowSubscriptions[i];
            if (!string.Equals(key, sessionKey, StringComparison.Ordinal))
            {
                continue;
            }

            this.rowSubscriptions[i] = (key, tab, tabHandler, newAgentHandler);
            break;
        }
    }

    private void UpdateRowThinking(string sessionKey, bool isThinking)
    {
        var row = this.Rows.FirstOrDefault(r =>
            string.Equals(r.SessionKey, sessionKey, StringComparison.Ordinal));
        if (row is not null)
        {
            row.IsThinking = isThinking;
        }
    }

    private int IndexOfRow(string sessionKey)
    {
        for (var i = 0; i < this.Rows.Count; i++)
        {
            if (string.Equals(this.Rows[i].SessionKey, sessionKey, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    public void Dispose()
    {
        this._disposed = true;
        this.table.RunningSessions.CollectionChanged -= this.OnSessionsChanged;

        foreach (var (_, tab, tabHandler, agentHandler) in this.rowSubscriptions)
        {
            tab.PropertyChanged -= tabHandler;
            if (agentHandler is not null && tab.Agent is { } agent)
            {
                agent.PropertyChanged -= agentHandler;
            }
        }

        this.rowSubscriptions.Clear();

        foreach (var (row, handler) in this.rowThinkingSubscriptions.Values)
        {
            row.PropertyChanged -= handler;
        }

        this.rowThinkingSubscriptions.Clear();

        foreach (var (history, handler) in this.historySubscriptions.Values)
        {
            ((INotifyCollectionChanged)history).CollectionChanged -= handler;
        }

        this.historySubscriptions.Clear();

        foreach (var (session, handler) in this.sessionMetadataSubscriptions.Values)
        {
            session.PropertyChanged -= handler;
        }

        this.sessionMetadataSubscriptions.Clear();
    }
}
