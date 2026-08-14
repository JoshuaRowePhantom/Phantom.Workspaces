using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
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
                    ? this.CreateTabRow(sessionKey, tabInfo)
                    : this.CreateFallbackRow(session);
                this.Rows.Add(row);
                this.SubscribeRowThinking(row);
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
                var replacement = this.CreateTabRow(sessionKey, tabInfo);
                this.Rows[rowIndex] = replacement;
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
                this.SubscribeRowThinking(replacement);
            }
            else if (hasTab)
            {
                existing.IsThinking = tabInfo.Tab.Agent?.IsChatRunning ?? false;
            }
        }

        this.RaisePropertyChanged(nameof(this.HasRows));
        this.ResortRows();
        this.RecomputeIsAnyAgentPulsating();
    }

    private RunningAgentRowViewModel CreateTabRow(string sessionKey, AgentTabInfo tabInfo)
    {
        var capturedTabId = tabInfo.Tab.Id;
        var capturedPaneId = tabInfo.PaneId;

        ICommand activateCmd = new RelayCommand(async _ =>
        {
            this.IsOpen = false;
            await this.navigator.NavigateAsync(
                new NavigationTarget
                {
                    TabId = capturedTabId,
                    WorkspacePaneId = capturedPaneId,
                    AgentSessionKey = sessionKey,
                },
                new NavigationOptions { OpenEntityIfNoTab = true });
        });

        return new RunningAgentRowViewModel(
            sessionKey: sessionKey,
            workspacePaneTitle: tabInfo.PaneTitle,
            tabTitle: tabInfo.Tab.Title,
            isThinking: tabInfo.Tab.Agent?.IsChatRunning ?? false,
            activateCommand: activateCmd,
            timeProvider: this.timeProvider);
    }

    private RunningAgentRowViewModel CreateFallbackRow(RunningAgentChatWithEntityInfo session)
    {
        var capturedSessionKey = session.SessionId.Value;

        ICommand activateCmd = new RelayCommand(async _ =>
        {
            this.IsOpen = false;
            await this.navigator.NavigateAsync(
                new NavigationTarget { AgentSessionKey = capturedSessionKey },
                new NavigationOptions { OpenEntityIfNoTab = true });
        });

        return new RunningAgentRowViewModel(
            sessionKey: capturedSessionKey,
            entityName: session.EntityName,
            activateCommand: activateCmd,
            timeProvider: this.timeProvider);
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
                    row.IsThinking = info.Tab.Agent?.IsChatRunning ?? false;
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
    }
}
