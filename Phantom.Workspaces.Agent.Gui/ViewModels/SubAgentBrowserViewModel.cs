using System.Collections.Specialized;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

/// <summary>
/// View model for the sub-agents browser card. Shows sub-agents sorted reverse-chronologically by
/// <see cref="IRunningSubAgent.LastUpdatedAt"/> and supports optional filtering via
/// <see cref="HideCompleted"/>.
/// </summary>
public sealed class SubAgentBrowserViewModel : ViewModelBase, IDisposable
{
    private readonly ReadOnlyObservableCollection<IRunningSubAgent> allSubAgents;
    private readonly Dictionary<IRunningSubAgent, EventHandler> completionHandlers =
        new(ReferenceEqualityComparer.Instance);
    private bool hideCompleted;
    private bool disposed;
    private IReadOnlyList<IRunningSubAgent> visibleItems = [];

    public SubAgentBrowserViewModel(ReadOnlyObservableCollection<IRunningSubAgent> allSubAgents)
    {
        this.allSubAgents = allSubAgents;
        ((INotifyCollectionChanged)allSubAgents).CollectionChanged += this.OnSubAgentsChanged;
        this.SyncSubscriptionsAndRefresh();
    }

    public bool HideCompleted
    {
        get => this.hideCompleted;
        set
        {
            if (this.SetProperty(ref this.hideCompleted, value))
            {
                this.RunOnUiThread(this.RefreshVisibleItems);
            }
        }
    }

    public IReadOnlyList<IRunningSubAgent> VisibleItems
    {
        get => this.visibleItems;
        private set => this.SetProperty(ref this.visibleItems, value);
    }

    public void Dispose()
    {
        if (this.disposed) return;
        this.disposed = true;
        ((INotifyCollectionChanged)this.allSubAgents).CollectionChanged -= this.OnSubAgentsChanged;
        foreach (var (agent, handler) in this.completionHandlers)
            agent.CompletionStateChanged -= handler;
        this.completionHandlers.Clear();
    }

    private void OnSubAgentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => this.RunOnUiThread(this.SyncSubscriptionsAndRefresh);

    private void OnCompletionStateChanged(object? sender, EventArgs e)
        => this.RunOnUiThread(this.RefreshVisibleItems);

    private void RunOnUiThread(Action action)
    {
        if (Avalonia.Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            if (!this.disposed) action();
        }
        else
        {
            Dispatcher.UIThread.Post(() => { if (!this.disposed) action(); });
        }
    }

    private void SyncSubscriptionsAndRefresh()
    {
        foreach (var agent in this.completionHandlers.Keys.Where(a => !this.allSubAgents.Contains(a)).ToList())
        {
            agent.CompletionStateChanged -= this.completionHandlers[agent];
            this.completionHandlers.Remove(agent);
        }

        foreach (var agent in this.allSubAgents)
        {
            if (this.completionHandlers.ContainsKey(agent)) continue;
            EventHandler handler = this.OnCompletionStateChanged;
            agent.CompletionStateChanged += handler;
            this.completionHandlers.Add(agent, handler);
        }

        this.RefreshVisibleItems();
    }

    private void RefreshVisibleItems()
    {
        IEnumerable<IRunningSubAgent> items = this.allSubAgents;

        if (this.hideCompleted)
        {
            items = items.Where(a => a.CompletionState == AgentChatCompletionState.Running);
        }

        this.VisibleItems = items.OrderByDescending(a => a.LastUpdatedAt).ToList();
    }
}
