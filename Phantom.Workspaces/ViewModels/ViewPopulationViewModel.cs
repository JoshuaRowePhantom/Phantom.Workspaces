using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

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
    private readonly List<SubscribedQuery> _querySubscriptions = [];

    public ObservableCollection<ViewEntityViewModel> Entities { get; } = [];

    public ObservableCollection<ViewEntityViewModel> RootEntities { get; } = [];

    internal CancellationToken CancellationToken => _cts.Token;

    internal void AddGetSubscription(SubscribedGet subscription) =>
        _getSubscriptions.Add(subscription);

    internal void AddQuerySubscription(SubscribedQuery subscription) =>
        _querySubscriptions.Add(subscription);

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _getSubscriptions.Clear();
        _querySubscriptions.Clear();
    }
}
