using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Session view model for the entity-View "find" (Ctrl-F) affordance. Owns the query, the ordered
/// match list, current index (with wrap-around), the previously-focused restore target, the set of
/// session-owned JSON-view switches, and the open/close/next/previous commands. Matching is
/// case-insensitive and JSON matching walks the structured <see cref="JsonElement"/> — property
/// names/keys are never tested (see <see cref="JsonValueMatcher"/>).
/// </summary>
public sealed class FindViewModel : ViewModelBase
{
    public enum MatchWhere
    {
        None,
        CardText,
        JsonOnly,
    }

    public readonly struct Match
    {
        public Match(EntityListNodeViewModel node, MatchWhere where)
        {
            this.Node = node;
            this.Where = where;
        }

        public EntityListNodeViewModel Node { get; }

        public MatchWhere Where { get; }
    }

    private ViewPopulationViewModel? population;
    private readonly EntityListViewModel? legacyList;
    private readonly Action<EntityCardViewModel>? bringIntoView;
    private readonly List<Match> matches = new();
    private readonly HashSet<EntityCardViewModel> sessionOpenedJsonCards = new();
    private string query = string.Empty;
    private bool isOpen;
    private bool hideUnmatched;
    private int currentIndex = -1;
    private EntityCardViewModel? restoreTarget;

    public FindViewModel(
        ViewPopulationViewModel? population = null,
        Action<EntityCardViewModel>? bringIntoView = null)
    {
        this.population = population;
        this.bringIntoView = bringIntoView;
        this.OpenCommand = new RelayCommand(_ => this.Open());
        this.CloseCommand = new RelayCommand(_ => this.Close());
        this.NextCommand = new RelayCommand(
            _ => this.Move(+1),
            _ => this.matches.Count > 0);
        this.PreviousCommand = new RelayCommand(
            _ => this.Move(-1),
            _ => this.matches.Count > 0);
    }

    /// <summary>
    /// Legacy constructor for the entity-browser tab path, which still operates on
    /// <see cref="EntityListViewModel"/>. Out of scope for #1256.
    /// </summary>
    public FindViewModel(
        EntityListViewModel list,
        Action<EntityCardViewModel>? bringIntoView = null)
    {
        this.legacyList = list;
        this.bringIntoView = bringIntoView;
        this.OpenCommand = new RelayCommand(_ => this.Open());
        this.CloseCommand = new RelayCommand(_ => this.Close());
        this.NextCommand = new RelayCommand(
            _ => this.Move(+1),
            _ => this.matches.Count > 0);
        this.PreviousCommand = new RelayCommand(
            _ => this.Move(-1),
            _ => this.matches.Count > 0);
    }

    public ViewPopulationViewModel? Population => this.population;

    public RelayCommand OpenCommand { get; }

    public RelayCommand CloseCommand { get; }

    public RelayCommand NextCommand { get; }

    public RelayCommand PreviousCommand { get; }

    public bool IsOpen
    {
        get => this.isOpen;
        private set => this.SetProperty(ref this.isOpen, value);
    }

    public bool HideUnmatched
    {
        get => this.hideUnmatched;
        set
        {
            if (this.SetProperty(ref this.hideUnmatched, value))
            {
                this.ApplyFilter();
            }
        }
    }

    public string Query
    {
        get => this.query;
        set
        {
            var previous = this.query;
            if (!this.SetProperty(ref this.query, value ?? string.Empty))
            {
                return;
            }

            var previousShortened = this.query.Length < previous.Length;
            this.Recompute(previousShortened);
        }
    }

    public string MatchStatusText
    {
        get
        {
            if (string.IsNullOrEmpty(this.query))
            {
                return string.Empty;
            }

            if (this.matches.Count == 0)
            {
                return "0 / 0";
            }

            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0} / {1}",
                this.currentIndex + 1,
                this.matches.Count);
        }
    }

    public IReadOnlyList<Match> Matches => this.matches;

    public int CurrentIndex => this.currentIndex;

    public Match? CurrentMatch =>
        this.currentIndex >= 0 && this.currentIndex < this.matches.Count
            ? this.matches[this.currentIndex]
            : (Match?)null;

    public EntityCardViewModel? CurrentCard => this.CurrentMatch?.Node.Card;

    /// <summary>
    /// Replaces the active population target. Called by MainWindowViewModel when
    /// CurrentViewPopulation swaps.
    /// </summary>
    public void SetPopulation(ViewPopulationViewModel? newPopulation)
    {
        this.population = newPopulation;
        this.ReapplyToPopulation();
    }

    /// <summary>
    /// Re-applies the active find state (query + hide-unmatched) to the current population.
    /// </summary>
    public void ReapplyToPopulation()
    {
        if (this.population is null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(this.query))
        {
            this.Recompute(previousShortened: false);
        }
        else
        {
            this.population.ApplyFind(null, this.hideUnmatched);
        }
    }

    public void Open()
    {
        if (this.IsOpen)
        {
            return;
        }

        this.restoreTarget = null;
        if (this.population is not null)
        {
            foreach (var entity in this.population.Entities)
            {
                if (entity.EntityCardNode.Card.IsSelected)
                {
                    this.restoreTarget = entity.EntityCardNode.Card;
                    break;
                }
            }
        }
        else if (this.legacyList is not null)
        {
            foreach (var item in this.legacyList.Items)
            {
                if (item.Node.Card.IsSelected)
                {
                    this.restoreTarget = item.Node.Card;
                    break;
                }
            }
        }

        this.IsOpen = true;
    }

    public void Close()
    {
        // Restore all visibility.
        if (this.population is not null)
        {
            this.population.ApplyFind(null, false);
        }
        else
        {
            this.legacyList?.ClearFindFilter();
        }

        // Revert only session-opened JSON views.
        foreach (var card in this.sessionOpenedJsonCards)
        {
            if (card != this.CurrentCard)
            {
                card.IsJsonVisible = false;
            }
        }
        this.sessionOpenedJsonCards.Clear();

        this.IsOpen = false;

        if (this.CurrentCard is { } card2)
        {
            this.bringIntoView?.Invoke(card2);
        }
    }

    private void Move(int delta)
    {
        if (this.matches.Count == 0)
        {
            return;
        }

        var count = this.matches.Count;
        this.currentIndex = ((this.currentIndex + delta) % count + count) % count;
        this.Activate();
        this.RaisePropertyChanged(nameof(this.CurrentIndex));
        this.RaisePropertyChanged(nameof(this.CurrentMatch));
        this.RaisePropertyChanged(nameof(this.CurrentCard));
        this.RaisePropertyChanged(nameof(this.MatchStatusText));
    }

    private static MatchWhere ComputeMatchWhere(EntityCardViewModel card, string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return MatchWhere.None;
        }

        var displayName = card.DisplayName;
        var entityType = card.EntityType;
        var inCardText =
            (!string.IsNullOrEmpty(displayName) && displayName.Contains(query, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrEmpty(entityType) && entityType.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (inCardText)
        {
            return MatchWhere.CardText;
        }

        if (card.Entity?.Data is JsonElement data
            && JsonValueMatcher.MatchesJsonValues(data, query))
        {
            return MatchWhere.JsonOnly;
        }

        return MatchWhere.None;
    }

    private void Recompute(bool previousShortened)
    {
        if (this.population is null && this.legacyList is null)
        {
            return;
        }

        var previousCard = this.CurrentCard;

        this.matches.Clear();

        if (this.population is not null)
        {
            foreach (var node in EnumerateNodes(this.population))
            {
                var where = ComputeMatchWhere(node.Card, this.query);
                if (where != MatchWhere.None)
                {
                    this.matches.Add(new Match(node, where));
                }
            }

            // Fan out search query and recompute visibility via the population.
            this.population.ApplyFind(
                string.IsNullOrEmpty(this.query) ? null : this.query,
                this.hideUnmatched);
        }
        else if (this.legacyList is not null)
        {
            foreach (var node in this.legacyList.EnumerateInOrder())
            {
                var where = ComputeMatchWhere(node.Card, this.query);
                if (where != MatchWhere.None)
                {
                    this.matches.Add(new Match(node, where));
                }

                node.Card.SearchQuery = this.query;
            }

            this.LegacyApplyFilter();
        }

        // Restore-current-on-backspace.
        int idx = -1;
        if (previousShortened)
        {
            if (previousCard is not null)
            {
                idx = this.matches.FindIndex(m => m.Node.Card == previousCard);
            }

            if (idx < 0 && this.restoreTarget is not null)
            {
                idx = this.matches.FindIndex(m => m.Node.Card == this.restoreTarget);
            }
        }

        if (idx < 0)
        {
            idx = this.matches.Count > 0 ? 0 : -1;
        }

        this.currentIndex = idx;

        this.Activate();

        this.NextCommand.RaiseCanExecuteChanged();
        this.PreviousCommand.RaiseCanExecuteChanged();
        this.RaisePropertyChanged(nameof(this.CurrentIndex));
        this.RaisePropertyChanged(nameof(this.CurrentMatch));
        this.RaisePropertyChanged(nameof(this.CurrentCard));
        this.RaisePropertyChanged(nameof(this.MatchStatusText));
    }

    private void ApplyFilter()
    {
        if (this.population is not null)
        {
            this.population.ApplyFind(
                string.IsNullOrEmpty(this.query) ? null : this.query,
                this.hideUnmatched);
        }
        else if (this.legacyList is not null)
        {
            this.LegacyApplyFilter();
        }
    }

    private void LegacyApplyFilter()
    {
        if (string.IsNullOrEmpty(this.query))
        {
            this.legacyList!.ClearFindFilter();
            return;
        }

        this.legacyList!.ApplyFindFilter(this.matches, this.hideUnmatched);
    }

    private void Activate()
    {
        if (this.population is not null)
        {
            foreach (var entity in this.population.Entities)
            {
                var card = entity.EntityCardNode.Card;
                if (card.IsSelected && card != this.CurrentCard)
                {
                    card.IsSelected = false;
                }
            }
        }
        else if (this.legacyList is not null)
        {
            foreach (var item in this.legacyList.Items)
            {
                if (item.Node.Card.IsSelected && item.Node.Card != this.CurrentCard)
                {
                    item.Node.Card.IsSelected = false;
                }
            }
        }

        // Revert any session-opened JSON views for cards other than the new current.
        var current = this.CurrentCard;
        var toRevert = new List<EntityCardViewModel>();
        foreach (var opened in this.sessionOpenedJsonCards)
        {
            if (opened != current)
            {
                toRevert.Add(opened);
            }
        }
        foreach (var opened in toRevert)
        {
            opened.IsJsonVisible = false;
            this.sessionOpenedJsonCards.Remove(opened);
        }

        if (this.CurrentMatch is not { } match)
        {
            return;
        }

        match.Node.Card.IsSelected = true;

        if (match.Where == MatchWhere.JsonOnly)
        {
            if (!match.Node.Card.IsJsonVisible)
            {
                match.Node.Card.IsJsonVisible = true;
                this.sessionOpenedJsonCards.Add(match.Node.Card);
            }
        }

        this.bringIntoView?.Invoke(match.Node.Card);
    }

    private static IEnumerable<EntityListNodeViewModel> EnumerateNodes(ViewPopulationViewModel population)
    {
        foreach (var root in population.RootEntities)
        {
            foreach (var node in EnumerateDepthFirst(root))
            {
                yield return node;
            }
        }
    }

    private static IEnumerable<EntityListNodeViewModel> EnumerateDepthFirst(ViewEntityViewModel entity)
    {
        yield return entity.EntityCardNode;
        foreach (var child in entity.Children)
        {
            foreach (var node in EnumerateDepthFirst(child))
            {
                yield return node;
            }
        }
    }
}
