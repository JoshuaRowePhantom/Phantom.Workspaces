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
    private readonly string description;
    private readonly Func<AgentChatCompletionState> getCompletionState;
    private readonly AgentChatRunningItemCollection runningItems;
    private readonly INotifyCollectionChanged subAgentsSource;
    private readonly Func<IRunningSubAgent, RunningSubAgentDisplay> childFactory;
    private readonly List<SubAgentActivityLine> recentActivity = [];
    private readonly List<ActivityKey> recentActivityKeys = [];
    private readonly ObservableCollection<IRunningSubAgentDisplay> subAgentDisplayItems = [];
    private readonly Dictionary<AgentChatRunningItem, NotifyCollectionChangedEventHandler> runningItemHandlers = new(ReferenceEqualityComparer<AgentChatRunningItem>.Instance);
    private readonly NotifyCollectionChangedEventHandler onRunningItemsChanged;
    private readonly NotifyCollectionChangedEventHandler onSubAgentsChanged;
    private readonly EventHandler? onAgentChatCompletionStateChanged;
    private readonly AgentChat? agentChat;

    public RunningSubAgentDisplay(AgentChat agentChat)
        : this(
            agentChat.AgentId,
            agentChat.DisplayName,
            agentChat.Description,
            () => agentChat.CompletionState,
            agentChat.RunningItems,
            (INotifyCollectionChanged)agentChat.SubAgents,
            subAgent => new RunningSubAgentDisplay((AgentChat)subAgent),
            agentChat)
    {
    }

    /// <summary>
    /// Internal constructor used by tests. Accepts the observable collections directly
    /// without requiring a fully initialised <see cref="AgentChat"/>.
    /// </summary>
    internal RunningSubAgentDisplay(
        AgentChatRunningItemCollection runningItems,
        string agentId = "",
        string displayName = "",
        string description = "")
        : this(
            agentId,
            displayName,
            description,
            () => AgentChatCompletionState.Running,
            runningItems,
            new System.Collections.ObjectModel.ObservableCollection<IRunningSubAgent>(),
            _ => throw new NotSupportedException("Child factory not provided in test constructor."),
            null)
    {
    }

    /// <summary>
    /// Creates a display that tracks only the given chat's own recent activity and completion
    /// state, without enumerating or wrapping its sub-agents. Used by the parent-agent panel
    /// (issue #902), where child sub-agents may be lazy <see cref="SubAgent"/> stubs that cannot be
    /// cast to <see cref="AgentChat"/>.
    /// </summary>
    internal static RunningSubAgentDisplay CreateActivityOnly(AgentChat agentChat)
        => new(
            agentChat.AgentId,
            agentChat.DisplayName,
            agentChat.Description,
            () => agentChat.CompletionState,
            agentChat.RunningItems,
            new ObservableCollection<IRunningSubAgent>(),
            _ => throw new NotSupportedException("Parent-activity display does not wrap sub-agents."),
            agentChat);

    private RunningSubAgentDisplay(
        string agentId,
        string displayName,
        string description,
        Func<AgentChatCompletionState> getCompletionState,
        AgentChatRunningItemCollection runningItems,
        INotifyCollectionChanged subAgentsSource,
        Func<IRunningSubAgent, RunningSubAgentDisplay> childFactory,
        AgentChat? agentChat)
    {
        this.agentId = agentId;
        this.displayName = displayName;
        this.description = description;
        this.getCompletionState = getCompletionState;
        this.runningItems = runningItems;
        this.subAgentsSource = subAgentsSource;
        this.childFactory = childFactory;
        this.agentChat = agentChat;
        this.SubAgents = new ReadOnlyObservableCollection<IRunningSubAgentDisplay>(this.subAgentDisplayItems);

        this.onRunningItemsChanged = this.OnRunningItemsChanged;
        this.onSubAgentsChanged = this.OnSubAgentsChanged;

        ((INotifyCollectionChanged)runningItems).CollectionChanged += this.onRunningItemsChanged;
        subAgentsSource.CollectionChanged += this.onSubAgentsChanged;
        
        if (agentChat is not null)
        {
            this.onAgentChatCompletionStateChanged = (sender, e) => this.CompletionStateChanged?.Invoke(this, e);
            agentChat.CompletionStateChanged += this.onAgentChatCompletionStateChanged;
        }

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
    public string Description => this.description;
    public AgentChatCompletionState CompletionState => this.getCompletionState();
    public IReadOnlyList<SubAgentActivityLine> RecentActivity => this.recentActivity;
    public ReadOnlyObservableCollection<IRunningSubAgentDisplay> SubAgents { get; }
    IReadOnlyList<IRunningSubAgentDisplay> IRunningSubAgentDisplay.SubAgents => this.SubAgents;

    public event EventHandler? ActivityChanged;

    public event EventHandler? CompletionStateChanged;

    private void OnRunningItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (AgentChatRunningItem item in e.OldItems)
            {
                if (!ContainsReference(e.NewItems, item))
                    this.UnsubscribeFromRunningItem(item);
            }
        }

        if (e.NewItems is not null)
        {
            foreach (AgentChatRunningItem item in e.NewItems)
            {
                if (!ContainsReference(e.OldItems, item))
                    this.SubscribeToRunningItem(item);
            }
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
        if (this.runningItemHandlers.TryGetValue(item, out var oldHandler))
        {
            item.Items.CollectionChanged -= oldHandler;
        }

        NotifyCollectionChangedEventHandler handler = (_, e) => this.OnRunningItemItemsChanged(item, e);
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

    private void OnRunningItemItemsChanged(AgentChatRunningItem item, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is null)
            return;

        var changed = false;
        var itemIndex = Math.Max(e.NewStartingIndex, 0);
        foreach (AgentChatHistoryItem historyItem in e.NewItems)
        {
            var line = CreateActivityLine(historyItem);
            if (line is null)
            {
                itemIndex++;
                continue;
            }

            var key = new ActivityKey(item, itemIndex);
            if (e.Action == NotifyCollectionChangedAction.Replace)
            {
                var existingIndex = this.recentActivityKeys.FindIndex(existing => existing.Equals(key));
                if (existingIndex >= 0)
                {
                    this.recentActivity[existingIndex] = line;
                    changed = true;
                }
                else
                {
                    this.AddRecentActivity(key, line);
                    changed = true;
                }
            }
            else if (e.Action == NotifyCollectionChangedAction.Add)
            {
                this.AddRecentActivity(key, line);
                changed = true;
            }

            itemIndex++;
        }

        if (changed)
            this.ActivityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AddRecentActivity(ActivityKey key, SubAgentActivityLine line)
    {
        if (this.recentActivity.Count == MaxActivityLines)
        {
            this.recentActivity.RemoveAt(0);
            this.recentActivityKeys.RemoveAt(0);
        }

        this.recentActivity.Add(line);
        this.recentActivityKeys.Add(key);
    }

    private static SubAgentActivityLine? CreateActivityLine(AgentChatHistoryItem historyItem)
    {
        var toolCall = historyItem.Contents.OfType<FunctionCallContent>().FirstOrDefault();
        if (toolCall is not null)
        {
            return new SubAgentActivityLine(SubAgentActivityKind.ToolCall, toolCall.Name ?? string.Empty);
        }

        var text = historyItem.Contents.OfType<TextContent>()
            .Select(t => t.Text)
            .FirstOrDefault(t => !string.IsNullOrEmpty(t));
        return text is not null
            ? new SubAgentActivityLine(SubAgentActivityKind.AgentText, text)
            : null;
    }

    private static bool ContainsReference(System.Collections.IList? items, AgentChatRunningItem item)
    {
        if (items is null)
        {
            return false;
        }

        foreach (var candidate in items)
        {
            if (ReferenceEquals(candidate, item))
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct ActivityKey(AgentChatRunningItem Item, int ItemIndex);

    public void Dispose()
    {
        ((INotifyCollectionChanged)this.runningItems).CollectionChanged -= this.onRunningItemsChanged;
        this.subAgentsSource.CollectionChanged -= this.onSubAgentsChanged;

        if (this.agentChat is not null && this.onAgentChatCompletionStateChanged is not null)
        {
            this.agentChat.CompletionStateChanged -= this.onAgentChatCompletionStateChanged;
        }

        foreach (var (item, handler) in this.runningItemHandlers)
            item.Items.CollectionChanged -= handler;

        this.runningItemHandlers.Clear();
        this.recentActivityKeys.Clear();

        foreach (var display in this.subAgentDisplayItems)
            if (display is IDisposable d)
                d.Dispose();
    }
}
