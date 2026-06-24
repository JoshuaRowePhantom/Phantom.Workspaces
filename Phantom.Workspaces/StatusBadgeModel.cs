using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Phantom.Workspaces;

/// <summary>
/// Holds the ordered collection of status badges shown on an entity card. Parallel to
/// <see cref="BadgesModel"/> (interest badges); status badges are display-only pills whose color
/// conveys good/bad/other and whose text is the status value.
/// </summary>
public sealed class StatusBadgesModel
{
    private readonly ObservableCollection<StatusBadgeModel> badges = [];

    public IReadOnlyList<StatusBadgeModel> Badges => this.badges;

    public void SetBadges(
        IEnumerable<StatusBadgeModel> badges)
    {
        this.badges.Clear();
        foreach (var badge in badges)
        {
            this.badges.Add(badge);
        }
    }
}

/// <summary>
/// A single status badge: a colored pill showing only the status value. The pill's background color
/// (resolved from <see cref="BrushKey"/>) conveys good/bad/other; the field name is carried in the
/// tooltip to disambiguate when an entity has multiple status fields.
/// </summary>
public sealed record StatusBadgeModel(
    string StatusValue,
    string BrushKey,
    string Tooltip);
