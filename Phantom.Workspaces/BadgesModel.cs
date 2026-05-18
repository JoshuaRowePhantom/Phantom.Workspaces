using System.Collections.ObjectModel;
using System.Collections.Generic;

namespace Phantom.Workspaces;

public sealed class BadgesModel
{
    private readonly ObservableCollection<BadgeModel> badges = [];

    public IReadOnlyList<BadgeModel> Badges => this.badges;

    public void SetBadges(
        IEnumerable<BadgeModel> badges)
    {
        this.badges.Clear();
        foreach (var badge in badges)
        {
            this.badges.Add(badge);
        }
    }
}

public sealed record BadgeModel(
    string InterestTypeEntityType,
    string Label,
    bool IsActive = true);
