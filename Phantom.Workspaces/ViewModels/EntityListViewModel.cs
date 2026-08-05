using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;

namespace Phantom.Workspaces.ViewModels;

public sealed class EntityListViewModel : ViewModelBase
{
    public ObservableCollection<EntityListItemViewModel> Items { get; } = [];

    /// <summary>
    /// Enumerates every node in tree order (matching the flat <see cref="Items"/> ordering by
    /// <c>Order</c>), yielding each node exactly once for find-cycling.
    /// </summary>
    public IEnumerable<EntityListNodeViewModel> EnumerateInOrder()
    {
        foreach (var item in this.Items)
        {
            yield return item.Node;
        }
    }

    /// <summary>
    /// Applies a find filter derived from a computed match set. Every ancestor of every match is
    /// marked <see cref="EntityListNodeViewModel.IsAncestorOfMatch"/> and force-expanded so match
    /// nodes remain reachable, and each node's <see cref="EntityListNodeViewModel.HideUnmatched"/>
    /// flag is set so its <c>VisibleChildren</c> hides purely-unmatched subtrees.
    /// </summary>
    public void ApplyFindFilter(
        IReadOnlyList<FindViewModel.Match> matches,
        bool hideUnmatched)
    {
        var byKey = this.Items.ToDictionary(
            static item => item.ItemKey,
            StringComparer.Ordinal);

        // Reset all filter state.
        foreach (var item in this.Items)
        {
            item.Node.MatchesFilter = false;
            item.Node.IsAncestorOfMatch = false;
        }

        var matchNodes = new HashSet<EntityListNodeViewModel>(matches.Select(m => m.Node));

        // Mark matches + ancestors and force-expand ancestors.
        foreach (var item in this.Items)
        {
            if (!matchNodes.Contains(item.Node))
            {
                continue;
            }

            item.Node.MatchesFilter = true;

            var parentKey = item.ParentItemKey;
            while (parentKey is not null && byKey.TryGetValue(parentKey, out var parent))
            {
                parent.Node.IsAncestorOfMatch = true;
                if (hideUnmatched)
                {
                    parent.IsExpanded = true;
                }
                parentKey = parent.ParentItemKey;
            }
        }

        // Apply per-node HideUnmatched and rebuild each node's VisibleChildren.
        foreach (var item in this.Items)
        {
            item.Node.HideUnmatched = hideUnmatched;
            item.Node.RefreshVisibleChildren();
        }
    }

    public void ClearFindFilter()
    {
        foreach (var item in this.Items)
        {
            item.Node.MatchesFilter = false;
            item.Node.IsAncestorOfMatch = false;
            item.Node.HideUnmatched = false;
            item.Node.RefreshVisibleChildren();
        }
    }

    public void SetItems(
        IReadOnlyCollection<EntityListItemViewModel> newItems)
    {
        var oldByKey = this.Items.ToDictionary(
            static item => item.ItemKey,
            StringComparer.Ordinal);

        var ordered = newItems.OrderBy(static item => item.Order).ToList();

        var newKeys = new HashSet<string>(
            ordered.Select(static item => item.ItemKey),
            StringComparer.Ordinal);
        foreach (var key in oldByKey.Keys.Where(k => !newKeys.Contains(k)).ToList())
        {
            this.Items.Remove(oldByKey[key]);
        }

        for (int i = 0; i < ordered.Count; i++)
        {
            var newItem = ordered[i];
            if (oldByKey.TryGetValue(newItem.ItemKey, out var existing))
            {
                existing.UpdateStructuralData(
                    newItem.Order,
                    newItem.Level,
                    newItem.ParentItemKey,
                    newItem.ChildItemKeys);
                existing.Node.SetChildren(newItem.Node.Children.ToList());
                existing.Node.SetImmediateChildKeys(newItem.Node.ImmediateChildKeys);
                existing.Node.SetHasChildren(newItem.Node.HasChildren);
                int currentIndex = this.Items.IndexOf(existing);
                if (currentIndex != i)
                {
                    this.Items.Move(currentIndex, i);
                }
                // Fire a Replace notification so that watchers that check item properties
                // (e.g. HasChildren) via CollectionChanged are re-evaluated after the update.
                this.Items[i] = existing;
            }
            else
            {
                this.Items.Insert(i, newItem);
            }
        }
    }
}
