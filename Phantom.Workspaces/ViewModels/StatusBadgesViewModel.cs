using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// View model wrapping a <see cref="StatusBadgesModel"/> so an entity card can bind to the ordered
/// status badges. Mirrors <see cref="BadgesViewModel"/>: it keeps an observable copy in sync with the
/// model's collection.
/// </summary>
public sealed class StatusBadgesViewModel : ViewModelBase
{
    private readonly StatusBadgesModel statusBadgesModel;

    public StatusBadgesViewModel(
        StatusBadgesModel statusBadgesModel)
    {
        this.statusBadgesModel = statusBadgesModel;
        this.Badges = new ObservableCollection<StatusBadgeModel>(statusBadgesModel.Badges);
        if (statusBadgesModel.Badges is INotifyCollectionChanged notifyCollectionChanged)
        {
            notifyCollectionChanged.CollectionChanged += this.OnBadgesChanged;
        }
    }

    public ObservableCollection<StatusBadgeModel> Badges { get; }

    private void OnBadgesChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        this.Badges.Clear();
        foreach (var badge in this.statusBadgesModel.Badges)
        {
            this.Badges.Add(badge);
        }
    }
}
