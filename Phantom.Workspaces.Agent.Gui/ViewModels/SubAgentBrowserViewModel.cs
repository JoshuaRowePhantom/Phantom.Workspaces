using System.Collections.Specialized;
using System.Collections.ObjectModel;
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
    private bool hideCompleted;
    private IReadOnlyList<IRunningSubAgent> visibleItems = [];

    public SubAgentBrowserViewModel(ReadOnlyObservableCollection<IRunningSubAgent> allSubAgents)
    {
        this.allSubAgents = allSubAgents;
        ((INotifyCollectionChanged)allSubAgents).CollectionChanged += this.OnSubAgentsChanged;
        this.RefreshVisibleItems();
    }

    public bool HideCompleted
    {
        get => this.hideCompleted;
        set
        {
            if (this.SetProperty(ref this.hideCompleted, value))
            {
                this.RefreshVisibleItems();
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
        ((INotifyCollectionChanged)this.allSubAgents).CollectionChanged -= this.OnSubAgentsChanged;
    }

    private void OnSubAgentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => this.RefreshVisibleItems();

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
