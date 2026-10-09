using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Holds mutable state and subscriptions for one view host (left navigation or a view tab).
/// Disposing the host's population cancels in-flight work and detaches live query observers.
/// </summary>
public sealed class ViewPopulationViewModel : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    internal SemaphoreSlim ReconcileGate { get; } = new(1, 1);
    private readonly List<SubscribedGet> _getSubscriptions = [];
    private readonly List<(SubscribedQuery Query, NotifyCollectionChangedEventHandler Handler)> _querySubscriptions = [];

    private string? findQuery;
    private bool hideUnmatched;

    public ViewPopulationViewModel(Dictionary<string, bool>? expandedEntityIds = null)
    {
        this.ExpandedEntityIds = expandedEntityIds ?? new Dictionary<string, bool>(StringComparer.Ordinal);
    }

    internal Dictionary<string, bool> ExpandedEntityIds { get; }

    internal int QueryObserverCount => _querySubscriptions.Count;

    public ObservableCollection<ViewEntityViewModel> Entities { get; } = [];

    public ObservableCollection<ViewEntityViewModel> RootEntities { get; } = [];

    internal CancellationToken CancellationToken => _cts.Token;

    public void ApplyFind(string? query, bool hideUnmatched)
    {
        this.findQuery = query;
        this.hideUnmatched = hideUnmatched;

        foreach (var root in this.RootEntities)
        {
            FanOutSearchQuery(root, query);
        }

        foreach (var root in this.RootEntities)
        {
            root.RecomputeVisibility(hideUnmatched);
        }

        NormalizeSelectionIfHidden();
    }

    private static void FanOutSearchQuery(ViewEntityViewModel node, string? query)
    {
        node.EntityCardNode.Card.SearchQuery = query;
        foreach (var child in node.Children)
        {
            FanOutSearchQuery(child, query);
        }
    }

    internal void ReapplyFindAfterAssembly() =>
        ApplyFind(this.findQuery, this.hideUnmatched);

    private void NormalizeSelectionIfHidden()
    {
        foreach (var entity in this.Entities.ToArray())
        {
            if (!entity.IsVisible && entity.EntityCardNode.Card.IsSelected)
            {
                entity.EntityCardNode.Card.IsSelected = false;
            }
        }
    }

    internal void AddGetSubscription(SubscribedGet subscription) =>
        _getSubscriptions.Add(subscription);

    /// <summary>
    /// Registers a live query subscription and observes its results so a populated query view rebinds
    /// when the query's membership changes (an entity entering or leaving the result set) without the
    /// user navigating away and back. The broker refreshes queries off the UI thread, so the rebind is
    /// marshaled to the UI thread and posted (rather than run inline) to rebuild the view after the
    /// broker's own collection mutation has completed.
    /// </summary>
    internal void AddQuerySubscription(
        SubscribedQuery subscription, Func<Task> onResultsChanged,
        ViewPopulationViewModel? lifetimeOwner = null)
    {
        var owner = lifetimeOwner ?? this;
        void Handler(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (owner._cts.IsCancellationRequested)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (owner._cts.IsCancellationRequested)
                {
                    return;
                }

                _ = onResultsChanged();
            });
        }

        subscription.Results.CollectionChanged += Handler;
        _querySubscriptions.Add((subscription, Handler));
    }

    /// <summary>
    /// Detaches the live query observers and clears the built entity lists so the owning view model can
    /// repopulate this same instance in place (no dispose/recreate) when a query's membership changes.
    /// </summary>
    internal void PrepareForRebuild()
    {
        this.PrepareForIncrementalReconcile();
        this.Entities.Clear();
        this.RootEntities.Clear();
    }

    internal void PrepareForIncrementalReconcile()
    {
        DetachQuerySubscriptions();
        _getSubscriptions.Clear();
    }

    internal void AdoptSubscriptions(ViewPopulationViewModel candidate)
    {
        this.PrepareForIncrementalReconcile();
        _getSubscriptions.AddRange(candidate._getSubscriptions);
        _querySubscriptions.AddRange(candidate._querySubscriptions);
        candidate._getSubscriptions.Clear();
        candidate._querySubscriptions.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        await this.ReconcileGate.WaitAsync();
        try
        {
            DetachQuerySubscriptions();
            _getSubscriptions.Clear();
            foreach (var entity in this.Entities.ToArray())
            {
                await entity.DisposeAsync();
            }
        }
        finally
        {
            this.ReconcileGate.Release();
        }
    }

    private void DetachQuerySubscriptions()
    {
        foreach (var (query, handler) in _querySubscriptions)
        {
            query.Results.CollectionChanged -= handler;
        }

        _querySubscriptions.Clear();
    }
}
