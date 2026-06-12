using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ViewModels;

public sealed class SubscribedEntityViewModel : ViewModelBase
{
    private EntitySnapshot snapshot;
    private bool isRawJsonVisible;
    private bool deleted;
    private readonly Func<SubscribedEntityViewModel, Task>? deleteEntityAsync;
    private readonly List<EntityDisplayItemViewModel> displayItems = [];

    public SubscribedEntityViewModel(
        EntitySnapshot snapshot,
        Func<SubscribedEntityViewModel, Task>? deleteEntityAsync = null)
    {
        this.snapshot = snapshot;
        this.deleted = snapshot.Data is null;
        this.deleteEntityAsync = deleteEntityAsync;
        this.displayItems.AddRange(EntityPresentation.GetDisplayItems(snapshot));
        this.DeleteEntityCommand = new RelayCommand(
            async _ => await this.DeleteEntityAsync(),
            _ => this.CanDeleteEntity);
        this.ToggleRawJsonVisibilityCommand = new RelayCommand(
            _ => this.ToggleRawJsonVisibility(),
            _ => this.CanToggleRawJson);
    }

    public BadgesModel Badges { get; } = new();

    public EntityId EntityId => this.snapshot.EntityId;

    public EntitySnapshot Snapshot
    {
        get => this.snapshot;
        private set
        {
            if (!this.SetProperty(ref this.snapshot, value))
            {
                return;
            }

            this.RaisePropertyChanged(nameof(this.DisplayName));
            this.RaisePropertyChanged(nameof(this.EntityType));
            this.RaisePropertyChanged(nameof(this.ModifiedTime));
            this.RaisePropertyChanged(nameof(this.ConcurrencyTag));
            this.RaisePropertyChanged(nameof(this.Data));
            this.RaisePropertyChanged(nameof(this.CanToggleRawJson));
            this.RaisePropertyChanged(nameof(this.Relationships));
            this.Deleted = value.Data is null;
            this.displayItems.Clear();
            this.displayItems.AddRange(EntityPresentation.GetDisplayItems(value));
            this.RaisePropertyChanged(nameof(this.DisplayItems));
            this.ToggleRawJsonVisibilityCommand.RaiseCanExecuteChanged();
            this.DeleteEntityCommand.RaiseCanExecuteChanged();
        }
    }

    public string DisplayName => EntityPresentation.GetDisplayName(this.snapshot);

    public string EntityType => EntityPresentation.GetEntityType(this.snapshot);

    public bool IsEntityType(
        string entityType)
    {
        return EntityPresentation.IsEntityType(this.snapshot, entityType);
    }

    public Timestamp ModifiedTime => this.snapshot.ModifiedTime;

    public ConcurrencyTag? ConcurrencyTag => this.snapshot.ConcurrencyTag;

    public JsonElement? Data => this.snapshot.Data;

    public IReadOnlyCollection<EntitySnapshot> Relationships => this.snapshot.Relationships;

    public IReadOnlyCollection<EntityDisplayItemViewModel> DisplayItems => this.displayItems;

    public RelayCommand DeleteEntityCommand { get; }

    public RelayCommand ToggleRawJsonVisibilityCommand { get; }

    public bool CanDeleteEntity => this.deleteEntityAsync is not null && !this.Deleted;

    public bool CanToggleRawJson => !this.Deleted && this.Data is JsonElement;

    public bool Deleted
    {
        get => this.deleted;
        private set
        {
            if (!this.SetProperty(ref this.deleted, value))
            {
                return;
            }

            if (value)
            {
                this.IsRawJsonVisible = false;
            }

            this.RaisePropertyChanged(nameof(this.CanDeleteEntity));
            this.RaisePropertyChanged(nameof(this.CanToggleRawJson));
            this.ToggleRawJsonVisibilityCommand.RaiseCanExecuteChanged();
            this.DeleteEntityCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsRawJsonVisible
    {
        get => this.isRawJsonVisible;
        private set => this.SetProperty(ref this.isRawJsonVisible, value);
    }

    internal void UpdateSnapshot(
        EntitySnapshot snapshot)
    {
        this.Snapshot = snapshot;
    }

    internal void MarkDeleted()
    {
        this.Deleted = true;
    }

    public async Task DeleteEntityAsync()
    {
        if (this.deleteEntityAsync is null)
        {
            return;
        }

        await this.deleteEntityAsync(this);
    }

    public void ToggleRawJsonVisibility()
    {
        if (!this.CanToggleRawJson)
        {
            return;
        }

        this.IsRawJsonVisible = !this.IsRawJsonVisible;
    }
}
