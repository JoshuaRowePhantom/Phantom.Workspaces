using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Holds all mutable state for one population run of the selected top-level view.
/// A new instance is created each time <c>ApplySelectedViewAsync</c> starts;
/// disposing the old instance cancels any in-flight work and releases broker subscriptions.
/// </summary>
public sealed class ViewPopulationViewModel : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly List<SubscribedGet> _getSubscriptions = [];
    private readonly List<(SubscribedQuery Query, NotifyCollectionChangedEventHandler Handler)> _querySubscriptions = [];

    private string? findQuery;
    private bool hideUnmatched;

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
        foreach (var entity in this.Entities)
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
    internal void AddQuerySubscription(SubscribedQuery subscription, Func<Task> onResultsChanged)
    {
        void Handler(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_cts.IsCancellationRequested)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (_cts.IsCancellationRequested)
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
        DetachQuerySubscriptions();
        _getSubscriptions.Clear();
        this.Entities.Clear();
        this.RootEntities.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        DetachQuerySubscriptions();
        _getSubscriptions.Clear();
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
