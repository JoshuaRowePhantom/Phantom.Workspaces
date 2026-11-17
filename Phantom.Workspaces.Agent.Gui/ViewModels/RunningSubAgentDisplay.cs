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
/// running item's <see cref="AgentChatRunningItem.Items"/> collection. Initial subscriptions and
/// snapshots use the engine's publication locks; the engine and viewer schedulers may differ.
/// </summary>
public sealed class RunningSubAgentDisplay : IRunningSubAgentDisplay, IDisposable
{
    /// <summary>Maximum number of recent activity lines to retain in the display buffer.</summary>
    public const int MaxActivityLines = 5;
    private readonly string agentId;
    private readonly string displayName;
    private readonly string description;
    private readonly string name;
    private readonly Func<AgentChatCompletionState> getCompletionState;
    private readonly AgentChatRunningItemCollection runningItems;
    private readonly INotifyCollectionChanged subAgentsSource;
    private readonly Func<IRunningSubAgent, IRunningSubAgentDisplay> childFactory;
    private readonly List<SubAgentActivityLine> recentActivity = [];
    private readonly List<ActivityKey> recentActivityKeys = [];
    private readonly ObservableCollection<IRunningSubAgentDisplay> subAgentDisplayItems = [];
    private readonly Dictionary<AgentChatRunningItem, NotifyCollectionChangedEventHandler> runningItemHandlers = new(ReferenceEqualityComparer<AgentChatRunningItem>.Instance);
    private readonly NotifyCollectionChangedEventHandler onRunningItemsChanged;
    private readonly NotifyCollectionChangedEventHandler onSubAgentsChanged;
    private readonly EventHandler? onAgentChatCompletionStateChanged;
    private readonly AgentChat? agentChat;
    private readonly object resourcesLock = new();
    private readonly NotifyCollectionChangedEventHandler onRunningItemChanged;
    private bool disposed;

    public RunningSubAgentDisplay(AgentChat agentChat)
        : this(
            agentChat.AgentId,
            agentChat.DisplayName,
            agentChat.Description,
            () => agentChat.CompletionState,
            agentChat.RunningItems,
            (INotifyCollectionChanged)agentChat.SubAgents,
            CreateChildDisplay,
            agentChat,
            name: agentChat.Name)
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
        string description = "",
        string name = "")
        : this(
            agentId,
            displayName,
            description,
            () => AgentChatCompletionState.Running,
            runningItems,
            new System.Collections.ObjectModel.ObservableCollection<IRunningSubAgent>(),
            _ => throw new NotSupportedException("Child factory not provided in test constructor."),
            null,
            name: name)
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
            agentChat,
            name: agentChat.Name);

    private RunningSubAgentDisplay(
        string agentId,
        string displayName,
        string description,
        Func<AgentChatCompletionState> getCompletionState,
        AgentChatRunningItemCollection runningItems,
        INotifyCollectionChanged subAgentsSource,
        Func<IRunningSubAgent, IRunningSubAgentDisplay> childFactory,
        AgentChat? agentChat,
        string name = "")
    {
        this.agentId = agentId;
        this.displayName = displayName;
        this.description = description;
        this.name = name;
        this.getCompletionState = getCompletionState;
        this.runningItems = runningItems;
        this.subAgentsSource = subAgentsSource;
        this.childFactory = childFactory;
        this.agentChat = agentChat;
        this.SubAgents = new ReadOnlyObservableCollection<IRunningSubAgentDisplay>(this.subAgentDisplayItems);

        this.onRunningItemsChanged = this.OnRunningItemsChanged;
        this.onSubAgentsChanged = this.OnSubAgentsChanged;
        this.onRunningItemChanged = (sender, change) =>
        {
            lock (this.resourcesLock)
            {
                if (this.disposed) return;
                var item = this.runningItemHandlers.Keys.FirstOrDefault(item => ReferenceEquals(item.Items, sender));
                if (item is not null) this.OnRunningItemItemsChanged(item, change);
            }
        };

        if (agentChat is not null)
        {
            this.onAgentChatCompletionStateChanged = (sender, e) => this.CompletionStateChanged?.Invoke(this, e);
        }

        try
        {
            if (agentChat is not null)
            {
                agentChat.CompletionStateChanged += this.onAgentChatCompletionStateChanged;
                ((IAgentChatRunningItemsSnapshotProvider)agentChat).SubscribeAndCaptureRunningItems(
                    this.onRunningItemsChanged,
                    this.onRunningItemChanged,
                    snapshots =>
                    {
                        lock (this.resourcesLock)
                            foreach (var snapshot in snapshots)
                                this.runningItemHandlers.Add(snapshot.Item, this.onRunningItemChanged);
                    });
            }
            else
            {
                ((INotifyCollectionChanged)runningItems).CollectionChanged += this.onRunningItemsChanged;
                foreach (var item in runningItems)
                    this.SubscribeToRunningItem(item);
            }

            if (agentChat is not null && ReferenceEquals(subAgentsSource, agentChat.SubAgents))
            {
                ((IAgentChatSubagentsSnapshotProvider)agentChat).SubscribeAndCaptureSubagents(
                    this.onSubAgentsChanged,
                    this.SeedChildren);
            }
            else
            {
                subAgentsSource.CollectionChanged += this.onSubAgentsChanged;
                this.SeedChildren((IEnumerable<IRunningSubAgent>)subAgentsSource);
            }
        }
        catch (Exception failure)
        {
            try { this.Dispose(); }
            catch (Exception cleanupFailure) { throw new AggregateException(failure, cleanupFailure); }
            throw;
        }
    }

    private void SeedChildren(IEnumerable<IRunningSubAgent> children)
    {
        lock (this.resourcesLock)
            foreach (var child in children)
                this.subAgentDisplayItems.Add(this.childFactory(child));
    }

    public string AgentId => this.agentId;
    public string DisplayName => this.displayName;
    public string Description => this.description;
    public string Name => this.name;
    public AgentChatCompletionState CompletionState => this.getCompletionState();
    public IReadOnlyList<SubAgentActivityLine> RecentActivity => this.recentActivity;
    public ReadOnlyObservableCollection<IRunningSubAgentDisplay> SubAgents { get; }
    IReadOnlyList<IRunningSubAgentDisplay> IRunningSubAgentDisplay.SubAgents => this.SubAgents;

    public event EventHandler? ActivityChanged;

    public event EventHandler? CompletionStateChanged;

    private static IRunningSubAgentDisplay CreateChildDisplay(IRunningSubAgent subAgent)
        => subAgent switch
        {
            AgentChat chat => new RunningSubAgentDisplay(chat),
            SubAgent { AgentChat: { } chat } => new RunningSubAgentDisplay(chat),
            _ => new RemoteRunningSubAgentDisplay(subAgent),
        };

    private void OnRunningItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        lock (this.resourcesLock)
        {
            if (this.disposed) return;
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
    }

    private void OnSubAgentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        lock (this.resourcesLock)
        {
            if (this.disposed) return;
            if (e.NewItems is not null)
            {
                foreach (IRunningSubAgent subAgent in e.NewItems)
                    this.subAgentDisplayItems.Add(this.childFactory(subAgent));
            }
        }
    }

    private void SubscribeToRunningItem(AgentChatRunningItem item)
    {
        if (this.runningItemHandlers.TryGetValue(item, out var oldHandler))
        {
            item.Items.CollectionChanged -= oldHandler;
        }

        var handler = this.onRunningItemChanged;
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
        List<Action> releases;
        lock (this.resourcesLock)
        {
            if (this.disposed) return;
            this.disposed = true;
            var items = this.runningItemHandlers.Keys.ToArray();
            releases =
            [
                () =>
                {
                    if (this.agentChat is not null)
                        ((IAgentChatRunningItemsSnapshotProvider)this.agentChat).UnsubscribeRunningItems(
                            this.onRunningItemsChanged,
                            this.onRunningItemChanged,
                            items);
                    else
                    {
                        ((INotifyCollectionChanged)this.runningItems).CollectionChanged -= this.onRunningItemsChanged;
                        foreach (var item in items)
                            item.Items.CollectionChanged -= this.onRunningItemChanged;
                    }
                },
                () => this.subAgentsSource.CollectionChanged -= this.onSubAgentsChanged,
                () =>
                {
                    if (this.agentChat is not null)
                        this.agentChat.CompletionStateChanged -= this.onAgentChatCompletionStateChanged;
                },
            ];
            releases.AddRange(this.subAgentDisplayItems.OfType<IDisposable>().Select<IDisposable, Action>(
                display => display.Dispose));
            releases.Add(this.subAgentDisplayItems.Clear);
            this.runningItemHandlers.Clear();
            this.recentActivity.Clear();
            this.recentActivityKeys.Clear();
        }
        ReleaseAll(releases);
    }

    internal static void ReleaseAll(IEnumerable<Action> releases)
    {
        List<Exception> failures = [];
        foreach (var release in releases)
        {
            try { release(); }
            catch (Exception failure) { failures.Add(failure); }
        }
        if (failures.Count != 0) throw new AggregateException(failures);
    }
}

internal sealed class RemoteRunningSubAgentDisplay : IRunningSubAgentDisplay, IDisposable
{
    private readonly IRunningSubAgent subAgent;
    private IReadOnlyList<IRunningSubAgentDisplay> children;
    private bool disposed;

    public RemoteRunningSubAgentDisplay(IRunningSubAgent subAgent)
    {
        this.subAgent = subAgent;
        this.children = CreateChildren(subAgent);
        try { subAgent.CompletionStateChanged += this.OnSubAgentChanged; }
        catch (Exception failure)
        {
            try { this.Dispose(); }
            catch (Exception cleanupFailure) { throw new AggregateException(failure, cleanupFailure); }
            throw;
        }
    }

    public string AgentId => this.subAgent.AgentId;
    public string DisplayName => this.subAgent.DisplayName;
    public string Description => this.subAgent.Description;
    public string Name => this.subAgent.Name;
    public AgentChatCompletionState CompletionState => this.subAgent.CompletionState;
    public IReadOnlyList<SubAgentActivityLine> RecentActivity => [];
    public IReadOnlyList<IRunningSubAgentDisplay> SubAgents => this.children;

    public event EventHandler? ActivityChanged { add { } remove { } }
    public event EventHandler? CompletionStateChanged;

    private void OnSubAgentChanged(object? sender, EventArgs e)
    {
        if (this.disposed) return;
        var replacement = CreateChildren(this.subAgent);
        var previous = this.children;
        this.children = replacement;
        RunningSubAgentDisplay.ReleaseAll(previous.OfType<IDisposable>().Select<IDisposable, Action>(
            child => child.Dispose));
        this.CompletionStateChanged?.Invoke(this, e);
    }

    private static IReadOnlyList<IRunningSubAgentDisplay> CreateChildren(IRunningSubAgent source)
    {
        List<IRunningSubAgentDisplay> children = [];
        try
        {
            foreach (var child in CaptureChildren(source))
                children.Add(new RemoteRunningSubAgentDisplay(child));
            return children;
        }
        catch (Exception failure)
        {
            try
            {
                RunningSubAgentDisplay.ReleaseAll(children.OfType<IDisposable>().Select<IDisposable, Action>(
                    child => child.Dispose));
            }
            catch (Exception cleanupFailure) { throw new AggregateException(failure, cleanupFailure); }
            throw;
        }
    }

    private static IReadOnlyList<IRunningSubAgent> CaptureChildren(IRunningSubAgent source)
    {
        if (source is not IAgentChatSubagentsSnapshotProvider snapshotProvider)
            return source.SubAgents;

        // A lazy SubAgent can materialize between display selection and recursive seeding.
        // Its children then belong to an engine, not to this viewer's scheduler.
        IReadOnlyList<IRunningSubAgent> children = [];
        NotifyCollectionChangedEventHandler ignoreChanges = static (_, _) => { };
        try
        {
            snapshotProvider.SubscribeAndCaptureSubagents(ignoreChanges, snapshot => children = snapshot);
            return children;
        }
        finally { snapshotProvider.UnsubscribeSubagents(ignoreChanges); }
    }

    public void Dispose()
    {
        if (this.disposed) return;
        this.disposed = true;
        var previous = this.children;
        this.children = [];
        RunningSubAgentDisplay.ReleaseAll(
            new Action[] { () => this.subAgent.CompletionStateChanged -= this.OnSubAgentChanged }
                .Concat(previous.OfType<IDisposable>().Select<IDisposable, Action>(child => child.Dispose)));
    }
}
