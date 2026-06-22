using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// A search result row for the entity-reference editor. Exposes the candidate's display name,
/// names, and id as copyable strings, plus a command to select it.
/// </summary>
public sealed class EntityReferenceCandidateViewModel : ViewModelBase
{
    private readonly Action<EntityReferenceCandidateViewModel> onSelected;

    public EntityReferenceCandidateViewModel(
        EntityReferenceCandidate candidate,
        Action<EntityReferenceCandidateViewModel> onSelected)
    {
        this.Candidate = candidate;
        this.onSelected = onSelected;
        this.SelectCommand = new RelayCommand(_ => this.onSelected(this));
    }

    public EntityReferenceCandidate Candidate { get; }

    public string DisplayName => this.Candidate.DisplayName;

    public string Names => this.Candidate.Names;

    public string EntityId => this.Candidate.EntityId;

    public RelayCommand SelectCommand { get; }
}

/// <summary>
/// Field editor for entity-reference (entity-id) fields. In read mode it shows the referenced
/// entity's display name with a tooltip of its names and id; in edit mode it offers a searchable
/// drop-down of candidates resolved via <see cref="IEntityReferenceSearch"/>.
/// </summary>
public sealed class EntityReferenceFieldEditorViewModel : EntityFieldEditorViewModel
{
    private readonly IEntityReferenceSearch? search;
    private readonly IReadOnlyCollection<string> entityTypes;
    private string value;
    private string resolvedDisplayName;
    private string tooltipText;
    private string searchText = string.Empty;

    public EntityReferenceFieldEditorViewModel(
        string fieldName,
        string? entityId,
        IReadOnlyCollection<string> entityTypes,
        IEntityReferenceSearch? search)
        : base(fieldName, "entity-reference")
    {
        this.value = entityId ?? string.Empty;
        this.entityTypes = entityTypes;
        this.search = search;
        this.resolvedDisplayName = this.value;
        this.tooltipText = this.value;
        this.Results = new ReadOnlyObservableCollection<EntityReferenceCandidateViewModel>(this.results);
    }

    private readonly ObservableCollection<EntityReferenceCandidateViewModel> results = [];

    public ReadOnlyObservableCollection<EntityReferenceCandidateViewModel> Results { get; }

    /// <summary>Raised after a search pass completes (used for deterministic tests).</summary>
    public event EventHandler? SearchCompleted;

    public string Value
    {
        get => this.value;
        private set
        {
            if (this.SetProperty(ref this.value, value))
            {
                this.RaisePropertyChanged(nameof(this.HasValue));
            }
        }
    }

    public bool HasValue => !string.IsNullOrEmpty(this.value);

    public string ResolvedDisplayName
    {
        get => this.resolvedDisplayName;
        private set => this.SetProperty(ref this.resolvedDisplayName, value);
    }

    public string TooltipText
    {
        get => this.tooltipText;
        private set => this.SetProperty(ref this.tooltipText, value);
    }

    public string SearchText
    {
        get => this.searchText;
        set
        {
            if (this.SetProperty(ref this.searchText, value))
            {
                _ = this.SearchAsync();
            }
        }
    }

    public IReadOnlyCollection<string> EntityTypes => this.entityTypes;

    /// <summary>
    /// Resolves the current value's display name and tooltip from the search abstraction.
    /// </summary>
    public async Task ResolveCurrentValueAsync()
    {
        if (this.search is null || string.IsNullOrEmpty(this.value))
        {
            return;
        }

        var candidate = await this.search.ResolveAsync(this.value).ConfigureAwait(true);
        if (candidate is not null)
        {
            this.ResolvedDisplayName = candidate.DisplayName;
            this.TooltipText = $"{candidate.Names}\n{candidate.EntityId}";
        }
    }

    /// <summary>
    /// Runs the entity search for the current <see cref="SearchText"/>, populating <see cref="Results"/>.
    /// </summary>
    public async Task SearchAsync()
    {
        if (this.search is null || string.IsNullOrWhiteSpace(this.searchText))
        {
            this.results.Clear();
            this.SearchCompleted?.Invoke(this, EventArgs.Empty);
            return;
        }

        var candidates = await this.search.SearchAsync(this.searchText, this.entityTypes).ConfigureAwait(true);
        this.results.Clear();
        foreach (var candidate in candidates)
        {
            this.results.Add(new EntityReferenceCandidateViewModel(candidate, this.OnCandidateSelected));
        }

        this.SearchCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void OnCandidateSelected(EntityReferenceCandidateViewModel candidate)
    {
        this.Value = candidate.EntityId;
        this.ResolvedDisplayName = candidate.DisplayName;
        this.TooltipText = $"{candidate.Names}\n{candidate.EntityId}";
        this.results.Clear();
    }

    public override EntityFieldEditorViewModel Clone()
    {
        var clone = new EntityReferenceFieldEditorViewModel(this.FieldName, this.value, this.entityTypes, this.search)
        {
            ResolvedDisplayName = this.resolvedDisplayName,
            TooltipText = this.tooltipText,
        };
        clone.SetEditMode(this.IsEditMode);
        return clone;
    }
}
