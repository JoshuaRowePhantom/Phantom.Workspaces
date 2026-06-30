using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    private readonly Action<Action> dispatch;

    private bool isAnyRunning;
    private bool isOpen;

    // Per-row subscriptions: tabId → (tab, tabHandler, agentHandler)
    private readonly List<(string tabId, AgentSessionWorkspaceTabViewModel tab,
        PropertyChangedEventHandler tabHandler,
        PropertyChangedEventHandler? agentHandler)> rowSubscriptions = [];

    public RunningAgentBrainViewModel(
        IRunningAgentChatTable table,
        Func<IEnumerable<AgentTabInfo>> getAllAgentTabs,
        Action<string, string?> activateTab,
        Action<Action> dispatch)
    {
        this.table = table;
        this.getAllAgentTabs = getAllAgentTabs;
        this.activateTab = activateTab;
        this.dispatch = dispatch;
        this.ToggleOpenCommand = new RelayCommand(_ => this.ToggleOpen());
        this.table.SessionsChanged += this.OnSessionsChanged;
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

    private void OnSessionsChanged(object? sender, EventArgs e) =>
        this.dispatch(this.Refresh);

    /// <summary>
    /// Rebuilds <see cref="Rows"/> from the current set of ready agent tabs,
    /// and updates <see cref="IsAnyRunning"/> from the table session count.
    /// </summary>
    public void Refresh()
    {
        this.IsAnyRunning = this.table.SessionCount > 0;

        // Scan all ready agent tabs, keyed by tab ID (each tab is one popup row).
        var currentTabs = this.getAllAgentTabs()
            .ToDictionary(info => info.Tab.Id, StringComparer.Ordinal);

        // Remove rows whose tabs are no longer present or no longer ready.
        var idsToRemove = this.Rows
            .Where(r => !currentTabs.ContainsKey(r.SessionKey))
            .Select(r => r.SessionKey)
            .ToList();

        foreach (var id in idsToRemove)
        {
            var rowIndex = this.IndexOfRow(id);
            if (rowIndex >= 0)
            {
                this.Rows.RemoveAt(rowIndex);
            }

            this.UnsubscribeRow(id);
        }

        // Add rows for tabs that don't yet have a row, and update IsThinking on existing rows.
        foreach (var (tabId, tabInfo) in currentTabs)
        {
            var existing = this.Rows.FirstOrDefault(r => string.Equals(r.SessionKey, tabId, StringComparison.Ordinal));
            var isThinking = tabInfo.Tab.Agent?.IsChatRunning ?? false;

            if (existing is null)
            {
                var row = this.CreateRow(tabId, tabInfo);
                this.Rows.Add(row);
                this.SubscribeRow(tabId, tabInfo.Tab);
            }
            else
            {
                existing.IsThinking = isThinking;
            }
        }

        this.RaisePropertyChanged(nameof(this.HasRows));
    }

    private RunningAgentRowViewModel CreateRow(string tabId, AgentTabInfo tabInfo)
    {
        var capturedTabId = tabInfo.Tab.Id;
        var capturedPaneId = tabInfo.PaneId;

        ICommand activateCmd = new RelayCommand(_ =>
        {
            this.IsOpen = false;
            this.activateTab(capturedTabId, capturedPaneId);
        });

        return new RunningAgentRowViewModel(
            sessionKey: tabId,
            workspacePaneTitle: tabInfo.PaneTitle,
            tabTitle: tabInfo.Tab.Title,
            isThinking: tabInfo.Tab.Agent?.IsChatRunning ?? false,
            activateCommand: activateCmd);
    }

    private void SubscribeRow(string tabId, AgentSessionWorkspaceTabViewModel tab)
    {
        PropertyChangedEventHandler? agentHandler = null;

        if (tab.Agent is { } agent)
        {
            agentHandler = CreateAgentHandler(tabId);
            agent.PropertyChanged += agentHandler;
        }

        PropertyChangedEventHandler tabHandler = (_, e) =>
        {
            if (e.PropertyName == nameof(AgentSessionWorkspaceTabViewModel.Agent))
            {
                this.UnsubscribeAgentHandler(tabId);
                if (tab.Agent is { } newAgent)
                {
                    this.UpdateRowThinking(tabId, newAgent.IsChatRunning);
                    var newAgentHandler = this.CreateAgentHandler(tabId);
                    newAgent.PropertyChanged += newAgentHandler;
                    this.UpdateRowAgentHandler(tabId, newAgentHandler);
                }
            }
        };

        tab.PropertyChanged += tabHandler;
        this.rowSubscriptions.Add((tabId, tab, tabHandler, agentHandler));
    }

    private PropertyChangedEventHandler CreateAgentHandler(string tabId) =>
        (_, e) =>
        {
            if (e.PropertyName == nameof(AgentViewModel.IsChatRunning))
            {
                var row = this.Rows.FirstOrDefault(r => string.Equals(r.SessionKey, tabId, StringComparison.Ordinal));
                if (row is not null && this.getAllAgentTabs().FirstOrDefault(t => t.Tab.Id == tabId) is { } info)
                {
                    row.IsThinking = info.Tab.Agent?.IsChatRunning ?? false;
                }
            }
        };

    private void UnsubscribeRow(string tabId)
    {
        for (var i = this.rowSubscriptions.Count - 1; i >= 0; i--)
        {
            var (id, tab, tabHandler, agentHandler) = this.rowSubscriptions[i];
            if (!string.Equals(id, tabId, StringComparison.Ordinal))
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

    private void UnsubscribeAgentHandler(string tabId)
    {
        for (var i = 0; i < this.rowSubscriptions.Count; i++)
        {
            var (id, tab, tabHandler, agentHandler) = this.rowSubscriptions[i];
            if (!string.Equals(id, tabId, StringComparison.Ordinal))
            {
                continue;
            }

            if (agentHandler is not null && tab.Agent is { } agent)
            {
                agent.PropertyChanged -= agentHandler;
            }

            this.rowSubscriptions[i] = (id, tab, tabHandler, null);
            break;
        }
    }

    private void UpdateRowAgentHandler(string tabId, PropertyChangedEventHandler newAgentHandler)
    {
        for (var i = 0; i < this.rowSubscriptions.Count; i++)
        {
            var (id, tab, tabHandler, _) = this.rowSubscriptions[i];
            if (!string.Equals(id, tabId, StringComparison.Ordinal))
            {
                continue;
            }

            this.rowSubscriptions[i] = (id, tab, tabHandler, newAgentHandler);
            break;
        }
    }

    private void UpdateRowThinking(string tabId, bool isThinking)
    {
        var row = this.Rows.FirstOrDefault(r => string.Equals(r.SessionKey, tabId, StringComparison.Ordinal));
        if (row is not null)
        {
            row.IsThinking = isThinking;
        }
    }

    private int IndexOfRow(string tabId)
    {
        for (var i = 0; i < this.Rows.Count; i++)
        {
            if (string.Equals(this.Rows[i].SessionKey, tabId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    public void Dispose()
    {
        this.table.SessionsChanged -= this.OnSessionsChanged;

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
