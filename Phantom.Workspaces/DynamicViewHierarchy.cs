using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces;

/// <summary>
/// A dynamic view hierarchy that maintains subscriptions to relationship traversals and rebuilds
/// when relationships change. Observes all member and parent traversal queries and raises a
/// Changed event when the hierarchy structure changes.
/// </summary>
public sealed class DynamicViewHierarchy : IDisposable
{
    private readonly IReadOnlyList<SubscribedEntityViewModel> roots;
    private readonly List<SubscribedQuery> subscriptions = [];
    private IReadOnlyList<ViewHierarchyNode> hierarchy = [];

    private DynamicViewHierarchy(
        IReadOnlyList<SubscribedEntityViewModel> roots,
        List<SubscribedQuery> subscriptions,
        IReadOnlyList<ViewHierarchyNode> initialHierarchy)
    {
        this.roots = roots;
        this.subscriptions = subscriptions;
        this.hierarchy = initialHierarchy;

        foreach (var subscription in subscriptions)
        {
            subscription.Results.CollectionChanged += this.OnSubscriptionChanged;
        }
    }

    /// <summary>The current hierarchy structure.</summary>
    public IReadOnlyList<ViewHierarchyNode> Hierarchy => this.hierarchy;

    /// <summary>Raised when the hierarchy structure changes due to relationship updates.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Creates a dynamic hierarchy for the given root entities by assembling their traversal
    /// subscriptions and observing changes.
    /// </summary>
    public static async Task<DynamicViewHierarchy> CreateAsync(
        EntityBroker entityBroker,
        IReadOnlyList<SubscribedEntityViewModel> roots,
        CancellationToken cancellationToken = default)
    {
        var subscriptions = new List<SubscribedQuery>();
        var assembler = new ViewHierarchyAssembler(entityBroker);
        
        // Build initial hierarchy and collect subscriptions
        var hierarchy = await assembler.AssembleWithSubscriptionsAsync(
            roots,
            subscriptions,
            cancellationToken).ConfigureAwait(false);

        return new DynamicViewHierarchy(roots, subscriptions, hierarchy);
    }

    public void Dispose()
    {
        foreach (var subscription in this.subscriptions)
        {
            subscription.Results.CollectionChanged -= this.OnSubscriptionChanged;
        }
    }

    private void OnSubscriptionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Rebuild the hierarchy with current subscription results
        this.hierarchy = ViewHierarchyAssembler.RebuildHierarchy(this.roots, this.subscriptions);
        this.Changed?.Invoke(this, EventArgs.Empty);
    }
}
