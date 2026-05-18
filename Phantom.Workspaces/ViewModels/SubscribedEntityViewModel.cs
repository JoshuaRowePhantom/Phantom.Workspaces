using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ViewModels;

public sealed class SubscribedEntityViewModel : ViewModelBase
{
    private EntitySnapshot snapshot;
    private readonly List<string> displayItems = [];

    public SubscribedEntityViewModel(
        EntitySnapshot snapshot)
    {
        this.snapshot = snapshot;
        this.displayItems.AddRange(EntityPresentation.GetDisplayItems(snapshot));
    }

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
            this.RaisePropertyChanged(nameof(this.Relationships));
            this.displayItems.Clear();
            this.displayItems.AddRange(EntityPresentation.GetDisplayItems(value));
            this.RaisePropertyChanged(nameof(this.DisplayItems));
        }
    }

    public string DisplayName => EntityPresentation.GetDisplayName(this.snapshot);

    public string EntityType => EntityPresentation.GetEntityType(this.snapshot);

    public Timestamp ModifiedTime => this.snapshot.ModifiedTime;

    public ConcurrencyTag? ConcurrencyTag => this.snapshot.ConcurrencyTag;

    public JsonElement? Data => this.snapshot.Data;

    public IReadOnlyCollection<EntitySnapshot> Relationships => this.snapshot.Relationships;

    public IReadOnlyCollection<string> DisplayItems => this.displayItems;

    internal void UpdateSnapshot(
        EntitySnapshot snapshot)
    {
        this.Snapshot = snapshot;
    }
}
