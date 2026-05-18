using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;

namespace Phantom.Workspaces.ViewModels;

public sealed class EntityListViewModel : ViewModelBase
{
    public ObservableCollection<EntityListItemViewModel> Items { get; } = [];

    public void SetItems(
        IReadOnlyCollection<EntityListItemViewModel> items)
    {
        this.Items.Clear();
        foreach (var item in items.OrderBy(static item => item.Order))
        {
            this.Items.Add(item);
        }
    }
}
