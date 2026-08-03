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

    private readonly EntityListViewModel list;
    private readonly Action<EntityCardViewModel>? bringIntoView;
    private readonly List<Match> matches = new();
    private readonly HashSet<EntityCardViewModel> sessionOpenedJsonCards = new();
    private string query = string.Empty;
    private bool isOpen;
    private bool hideUnmatched;
    private int currentIndex = -1;
    private EntityCardViewModel? restoreTarget;

    public FindViewModel(
        EntityListViewModel list,
        Action<EntityCardViewModel>? bringIntoView = null)
    {
        this.list = list;
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

    public EntityListViewModel List => this.list;

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

    public void Open()
    {
        if (this.IsOpen)
        {
            return;
        }

        // Capture the card that was selected before the session started, so backspace can
        // restore it as the query shortens.
        this.restoreTarget = null;
        foreach (var item in this.list.Items)
        {
            if (item.Node.Card.IsSelected)
            {
                this.restoreTarget = item.Node.Card;
                break;
            }
        }

        this.IsOpen = true;
    }

    public void Close()
    {
        // Restore all visibility, but keep the current selection on the found item.
        this.list.ClearFindFilter();

        // Revert only session-opened JSON views. Do not touch cards the user had open before.
        foreach (var card in this.sessionOpenedJsonCards)
        {
            if (card != this.CurrentCard)
            {
                card.IsJsonVisible = false;
            }
        }
        this.sessionOpenedJsonCards.Clear();

        this.IsOpen = false;

        // Leave selection on the last found item, kept in view.
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
        // #1200: guard against empty display-name / entity-type explicitly. "".Contains(query)
        // is always false for non-empty query, so an empty display name would silently zero
        // the card-text branch even if the primary fix regresses.
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
        var previousCard = this.CurrentCard;

        this.matches.Clear();
        foreach (var node in this.list.EnumerateInOrder())
        {
            var where = ComputeMatchWhere(node.Card, this.query);
            if (where != MatchWhere.None)
            {
                this.matches.Add(new Match(node, where));
            }

            // Highlight the match query on every card so the highlight run appears / disappears.
            node.Card.MatchQuery = this.query;
        }

        // Restore-current-on-backspace: if the previously current entity is now (again) a match
        // and the user is deleting characters, restore selection to it. Otherwise, prefer the
        // pre-session restore target if it is a match again.
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

        this.ApplyFilter();
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
        if (string.IsNullOrEmpty(this.query))
        {
            this.list.ClearFindFilter();
            return;
        }

        this.list.ApplyFindFilter(this.matches, this.hideUnmatched);
    }

    private void Activate()
    {
        // Clear IsSelected on every card in the list to reset prior selection.
        foreach (var item in this.list.Items)
        {
            if (item.Node.Card.IsSelected && item.Node.Card != this.CurrentCard)
            {
                item.Node.Card.IsSelected = false;
            }
        }

        // Revert any session-opened JSON views for cards other than the new current, so
        // navigating away restores card view (only for cards the session opened).
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
}
