using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Services;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Backs the brain-icon toolbar button and popup that lists every active agent session.
/// </summary>
internal sealed class RunningAgentBrainViewModel : ViewModelBase, IDisposable
{
    private readonly IRunningAgentChatTable table;
    private readonly Func<IEnumerable<AgentTabInfo>> getAllAgentTabs;
    private readonly Action<string, string?> activateTab;
    private readonly Action<string> openAgentForSession;
    private readonly Action<Action> dispatch;

    private bool isAnyRunning;
    private bool isOpen;
    private bool _disposed;

    // Per-row subscriptions: sessionKey → (tab, tabHandler, agentHandler)
    private readonly List<(string sessionKey, AgentSessionWorkspaceTabViewModel tab,
        PropertyChangedEventHandler tabHandler,
        PropertyChangedEventHandler? agentHandler)> rowSubscriptions = [];

    public RunningAgentBrainViewModel(
        IRunningAgentChatTable table,
        Func<IEnumerable<AgentTabInfo>> getAllAgentTabs,
        Action<string, string?> activateTab,
        Action<string> openAgentForSession,
        Action<Action> dispatch)
    {
        this.table = table;
        this.getAllAgentTabs = getAllAgentTabs;
        this.activateTab = activateTab;
        this.openAgentForSession = openAgentForSession;
        this.dispatch = dispatch;
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

    /// <summary>
    /// Rebuilds <see cref="Rows"/> from <see cref="IRunningAgentChatTable.RunningSessions"/>,
    /// pairing each session with its open tab (if any) or creating a fallback row.
    /// Also updates <see cref="IsAnyRunning"/>.
    /// </summary>
    public void Refresh()
    {
        if (this._disposed) return;
        this.IsAnyRunning = this.table.RunningSessions.Count > 0;

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
            this.table.RunningSessions.Select(s => s.SessionId.Value),
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

            this.UnsubscribeRow(key);
        }

        // Add or update a row for each active session
        foreach (var session in this.table.RunningSessions.ToList())
        {
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
                if (hasTab)
                {
                    this.SubscribeRow(sessionKey, tabInfo.Tab);
                }
            }
            else if (hasTab && !existing.HasOpenTab)
            {
                // Fallback → tab row: tab appeared for this session
                this.UnsubscribeRow(sessionKey);
                var rowIndex = this.IndexOfRow(sessionKey);
                this.Rows[rowIndex] = this.CreateTabRow(sessionKey, tabInfo);
                this.SubscribeRow(sessionKey, tabInfo.Tab);
            }
            else if (!hasTab && existing.HasOpenTab)
            {
                // Tab row → fallback: tab disappeared but session is still running
                this.UnsubscribeRow(sessionKey);
                var rowIndex = this.IndexOfRow(sessionKey);
                this.Rows[rowIndex] = this.CreateFallbackRow(session);
            }
            else if (hasTab)
            {
                existing.IsThinking = tabInfo.Tab.Agent?.IsChatRunning ?? false;
            }
        }

        this.RaisePropertyChanged(nameof(this.HasRows));
    }

    private RunningAgentRowViewModel CreateTabRow(string sessionKey, AgentTabInfo tabInfo)
    {
        var capturedTabId = tabInfo.Tab.Id;
        var capturedPaneId = tabInfo.PaneId;

        ICommand activateCmd = new RelayCommand(_ =>
        {
            this.IsOpen = false;
            this.activateTab(capturedTabId, capturedPaneId);
        });

        return new RunningAgentRowViewModel(
            sessionKey: sessionKey,
            workspacePaneTitle: tabInfo.PaneTitle,
            tabTitle: tabInfo.Tab.Title,
            isThinking: tabInfo.Tab.Agent?.IsChatRunning ?? false,
            activateCommand: activateCmd);
    }

    private RunningAgentRowViewModel CreateFallbackRow(RunningAgentChatWithEntityInfo session)
    {
        var capturedSessionKey = session.SessionId.Value;

        ICommand activateCmd = new RelayCommand(_ =>
        {
            this.IsOpen = false;
            this.openAgentForSession(capturedSessionKey);
        });

        return new RunningAgentRowViewModel(
            sessionKey: capturedSessionKey,
            entityName: session.EntityName,
            activateCommand: activateCmd);
    }

    private void SubscribeRow(string sessionKey, AgentSessionWorkspaceTabViewModel tab)
    {
        PropertyChangedEventHandler? agentHandler = null;

        if (tab.Agent is { } agent)
        {
            agentHandler = CreateAgentHandler(sessionKey);
            agent.PropertyChanged += agentHandler;
        }

        PropertyChangedEventHandler tabHandler = (_, e) =>
        {
            if (e.PropertyName == nameof(AgentSessionWorkspaceTabViewModel.Agent))
            {
                this.UnsubscribeAgentHandler(sessionKey);
                if (tab.Agent is { } newAgent)
                {
                    this.UpdateRowThinking(sessionKey, newAgent.IsChatRunning);
                    var newAgentHandler = this.CreateAgentHandler(sessionKey);
                    newAgent.PropertyChanged += newAgentHandler;
                    this.UpdateRowAgentHandler(sessionKey, newAgentHandler);
                }
            }
        };

        tab.PropertyChanged += tabHandler;
        this.rowSubscriptions.Add((sessionKey, tab, tabHandler, agentHandler));
    }

    private PropertyChangedEventHandler CreateAgentHandler(string sessionKey) =>
        (_, e) =>
        {
            if (e.PropertyName == nameof(AgentViewModel.IsChatRunning))
            {
                var row = this.Rows.FirstOrDefault(r =>
                    string.Equals(r.SessionKey, sessionKey, StringComparison.Ordinal));
                if (row is not null && this.getAllAgentTabs().FirstOrDefault(
                        t => string.Equals(t.Tab.AgentSessionId, sessionKey, StringComparison.Ordinal)) is { } info)
                {
                    row.IsThinking = info.Tab.Agent?.IsChatRunning ?? false;
                }
            }
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
    }
}
