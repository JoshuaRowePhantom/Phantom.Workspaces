using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;

namespace Phantom.Workspaces.ViewModels;

public sealed class EntityListViewModel : ViewModelBase
{
    public ObservableCollection<EntityListItemViewModel> Items { get; } = [];

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
