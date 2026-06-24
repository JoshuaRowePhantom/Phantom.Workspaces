using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// A single referenced entity within an <see cref="EntityListFieldEditorViewModel"/>. Exposes the
/// referenced entity's resolved display name and a command to open it.
/// </summary>
public sealed class EntityReferenceListItemViewModel : ViewModelBase
{
    private readonly Action<string>? openEntity;
    private string displayName;
    private string tooltipText;

    public EntityReferenceListItemViewModel(
        string entityId,
        Action<string>? openEntity)
    {
        this.EntityId = entityId;
        this.openEntity = openEntity;
        this.displayName = entityId;
        this.tooltipText = entityId;
        this.OpenCommand = new RelayCommand(
            _ => this.openEntity?.Invoke(this.EntityId),
            _ => this.CanOpen);
    }

    public string EntityId { get; }

    public string DisplayName
    {
        get => this.displayName;
        set => this.SetProperty(ref this.displayName, value);
    }

    public string TooltipText
    {
        get => this.tooltipText;
        set => this.SetProperty(ref this.tooltipText, value);
    }

    public bool CanOpen => !string.IsNullOrEmpty(this.EntityId) && this.openEntity is not null;

    public RelayCommand OpenCommand { get; }
}

/// <summary>
/// Field editor for a list of entity-id references (a <c>core.json#/$defs/entity-id-list</c> field).
/// In read mode it renders the referenced entities' display names as a plain list of navigable links;
/// each member resolves its display name via <see cref="IEntityReferenceSearch"/>.
/// </summary>
public sealed class EntityListFieldEditorViewModel : EntityFieldEditorViewModel
{
    private readonly IReadOnlyList<string> entityIds;
    private readonly IReadOnlyCollection<string> entityTypes;
    private readonly IEntityReferenceSearch? search;
    private readonly Action<string>? openEntity;
    private readonly ObservableCollection<EntityReferenceListItemViewModel> items = [];

    public EntityListFieldEditorViewModel(
        string fieldName,
        IReadOnlyList<string> entityIds,
        IReadOnlyCollection<string> entityTypes,
        IEntityReferenceSearch? search,
        Action<string>? openEntity = null)
        : base(fieldName, "entity-list")
    {
        this.entityIds = entityIds;
        this.entityTypes = entityTypes;
        this.search = search;
        this.openEntity = openEntity;
        foreach (var entityId in entityIds)
        {
            this.items.Add(new EntityReferenceListItemViewModel(entityId, openEntity));
        }

        this.Items = new ReadOnlyObservableCollection<EntityReferenceListItemViewModel>(this.items);
    }

    public ReadOnlyObservableCollection<EntityReferenceListItemViewModel> Items { get; }

    public bool HasItems => this.items.Count > 0;

    public IReadOnlyCollection<string> EntityTypes => this.entityTypes;

    /// <summary>
    /// Resolves each member's display name and tooltip from the search abstraction.
    /// </summary>
    public async Task ResolveDisplayNamesAsync()
    {
        if (this.search is null)
        {
            return;
        }

        foreach (var item in this.items)
        {
            if (string.IsNullOrEmpty(item.EntityId))
            {
                continue;
            }

            var candidate = await this.search.ResolveAsync(item.EntityId).ConfigureAwait(true);
            if (candidate is not null)
            {
                item.DisplayName = candidate.DisplayName;
                item.TooltipText = $"{candidate.Names}\n{candidate.EntityId}";
            }
        }
    }

    public override EntityFieldEditorViewModel Clone()
    {
        var clone = new EntityListFieldEditorViewModel(
            this.FieldName,
            this.entityIds,
            this.entityTypes,
            this.search,
            this.openEntity);
        for (var index = 0; index < this.items.Count && index < clone.items.Count; index++)
        {
            clone.items[index].DisplayName = this.items[index].DisplayName;
            clone.items[index].TooltipText = this.items[index].TooltipText;
        }

        clone.IsEditMode = this.IsEditMode;
        return clone;
    }
}
