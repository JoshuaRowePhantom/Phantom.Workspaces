using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels.Collections;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

/// <summary>
/// UI-layer adapter that wraps an <see cref="AgentChat"/> and builds its display model by
/// observing the model directly. Subscribes to <see cref="AgentChat.RunningItems"/> and each
/// running item's <see cref="AgentChatRunningItem.Items"/> collection. Because
/// <see cref="AgentChat"/> dispatches all <see cref="AgentChat.RunningItems"/> mutations on the
/// foreground scheduler, all notifications arrive on the UI thread — no additional marshalling
/// is needed.
/// </summary>
public sealed class RunningSubAgentDisplay : IRunningSubAgentDisplay, IDisposable
{
    /// <summary>Maximum number of recent activity lines to retain in the display buffer.</summary>
    public const int MaxActivityLines = 5;
    private readonly string agentId;
    private readonly string displayName;
    private readonly Func<AgentChatCompletionState> getCompletionState;
    private readonly AgentChatRunningItemCollection runningItems;
    private readonly INotifyCollectionChanged subAgentsSource;
    private readonly Func<IRunningSubAgent, RunningSubAgentDisplay> childFactory;
    private readonly List<SubAgentActivityLine> recentActivity = [];
    private readonly ObservableCollection<IRunningSubAgentDisplay> subAgentDisplayItems = [];
    private readonly Dictionary<AgentChatRunningItem, NotifyCollectionChangedEventHandler> runningItemHandlers = new(ReferenceEqualityComparer<AgentChatRunningItem>.Instance);
    private readonly NotifyCollectionChangedEventHandler onRunningItemsChanged;
    private readonly NotifyCollectionChangedEventHandler onSubAgentsChanged;

    public RunningSubAgentDisplay(AgentChat agentChat)
        : this(
            agentChat.AgentId,
            agentChat.DisplayName,
            () => agentChat.CompletionState,
            agentChat.RunningItems,
            (INotifyCollectionChanged)agentChat.SubAgents,
            subAgent => new RunningSubAgentDisplay((AgentChat)subAgent))
    {
    }

    /// <summary>
    /// Internal constructor used by tests. Accepts the observable collections directly
    /// without requiring a fully initialised <see cref="AgentChat"/>.
    /// </summary>
    internal RunningSubAgentDisplay(
        AgentChatRunningItemCollection runningItems,
        string agentId = "",
        string displayName = "")
        : this(
            agentId,
            displayName,
            () => AgentChatCompletionState.Running,
            runningItems,
            new System.Collections.ObjectModel.ObservableCollection<IRunningSubAgent>(),
            _ => throw new NotSupportedException("Child factory not provided in test constructor."))
    {
    }

    private RunningSubAgentDisplay(
        string agentId,
        string displayName,
        Func<AgentChatCompletionState> getCompletionState,
        AgentChatRunningItemCollection runningItems,
        INotifyCollectionChanged subAgentsSource,
        Func<IRunningSubAgent, RunningSubAgentDisplay> childFactory)
    {
        this.agentId = agentId;
        this.displayName = displayName;
        this.getCompletionState = getCompletionState;
        this.runningItems = runningItems;
        this.subAgentsSource = subAgentsSource;
        this.childFactory = childFactory;
        this.SubAgents = new ReadOnlyObservableCollection<IRunningSubAgentDisplay>(this.subAgentDisplayItems);

        this.onRunningItemsChanged = this.OnRunningItemsChanged;
        this.onSubAgentsChanged = this.OnSubAgentsChanged;

        ((INotifyCollectionChanged)runningItems).CollectionChanged += this.onRunningItemsChanged;
        subAgentsSource.CollectionChanged += this.onSubAgentsChanged;

        foreach (var item in runningItems)
            this.SubscribeToRunningItem(item);

        if (subAgentsSource is IEnumerable<IRunningSubAgent> existingSubAgents)
        {
            foreach (var subAgent in existingSubAgents)
                this.subAgentDisplayItems.Add(this.childFactory(subAgent));
        }
    }

    public string AgentId => this.agentId;
    public string DisplayName => this.displayName;
    public AgentChatCompletionState CompletionState => this.getCompletionState();
    public IReadOnlyList<SubAgentActivityLine> RecentActivity => this.recentActivity;
    public ReadOnlyObservableCollection<IRunningSubAgentDisplay> SubAgents { get; }
    IReadOnlyList<IRunningSubAgentDisplay> IRunningSubAgentDisplay.SubAgents => this.SubAgents;

    public event EventHandler? ActivityChanged;

    private void OnRunningItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (AgentChatRunningItem item in e.NewItems)
                this.SubscribeToRunningItem(item);
        }

        if (e.OldItems is not null)
        {
            foreach (AgentChatRunningItem item in e.OldItems)
                this.UnsubscribeFromRunningItem(item);
        }
    }

    private void OnSubAgentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (IRunningSubAgent subAgent in e.NewItems)
                this.subAgentDisplayItems.Add(this.childFactory(subAgent));
        }
    }

    private void SubscribeToRunningItem(AgentChatRunningItem item)
    {
        NotifyCollectionChangedEventHandler handler = (_, e) => this.OnRunningItemItemsChanged(e);
        this.runningItemHandlers[item] = handler;
        item.Items.CollectionChanged += handler;
    }

    private void UnsubscribeFromRunningItem(AgentChatRunningItem item)
    {
        if (this.runningItemHandlers.TryGetValue(item, out var handler))
        {
            item.Items.CollectionChanged -= handler;
            this.runningItemHandlers.Remove(item);
        }
    }

    private void OnRunningItemItemsChanged(NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is null)
            return;

        var added = false;
        foreach (AgentChatHistoryItem historyItem in e.NewItems)
        {
            SubAgentActivityLine? line = null;

            var toolCall = historyItem.Contents.OfType<FunctionCallContent>().FirstOrDefault();
            if (toolCall is not null)
            {
                line = new SubAgentActivityLine(SubAgentActivityKind.ToolCall, toolCall.Name ?? string.Empty);
            }
            else
            {
                var text = historyItem.Contents.OfType<TextContent>()
                    .Select(t => t.Text)
                    .FirstOrDefault(t => !string.IsNullOrEmpty(t));
                if (text is not null)
                    line = new SubAgentActivityLine(SubAgentActivityKind.AgentText, text);
            }

            if (line is null)
                continue;

            if (this.recentActivity.Count == MaxActivityLines)
                this.recentActivity.RemoveAt(0);

            this.recentActivity.Add(line);
            added = true;
        }

        if (added)
            this.ActivityChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        ((INotifyCollectionChanged)this.runningItems).CollectionChanged -= this.onRunningItemsChanged;
        this.subAgentsSource.CollectionChanged -= this.onSubAgentsChanged;

        foreach (var (item, handler) in this.runningItemHandlers)
            item.Items.CollectionChanged -= handler;

        this.runningItemHandlers.Clear();

        foreach (var display in this.subAgentDisplayItems)
            if (display is IDisposable d)
                d.Dispose();
    }
}
