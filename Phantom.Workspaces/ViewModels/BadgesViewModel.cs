using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace Phantom.Workspaces.ViewModels;

public sealed class BadgesViewModel : ViewModelBase
{
    private readonly BadgesModel badgesModel;

    public BadgesViewModel(
        BadgesModel badgesModel)
    {
        this.badgesModel = badgesModel;
        this.Badges = new ObservableCollection<BadgeModel>(badgesModel.Badges);
        if (badgesModel.Badges is INotifyCollectionChanged notifyCollectionChanged)
        {
            notifyCollectionChanged.CollectionChanged += this.OnBadgesChanged;
    }
    }

    public ObservableCollection<BadgeModel> Badges { get; }

    private void OnBadgesChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        this.Badges.Clear();
        foreach (var badge in this.badgesModel.Badges)
        {
            this.Badges.Add(badge);
        }
    }
}
