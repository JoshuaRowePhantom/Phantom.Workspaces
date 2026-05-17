using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces;

public sealed class EntityBroker
{
    private readonly EntityRepository entityRepository;
    private Dictionary<EntityId, EntitySnapshot> snapshotsById = new();

    public EntityBroker(
        EntityRepository entityRepository)
    {
        this.entityRepository = entityRepository;
    }

    public event EventHandler<EntityBrokerChangedEventArgs>? Changed;

    public IReadOnlyDictionary<EntityId, EntitySnapshot> SnapshotsById => this.snapshotsById;

    public async Task InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        await this.RefreshInternalAsync(cancellationToken);
    }

    public async Task RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        await this.RefreshInternalAsync(cancellationToken);
    }

    private async Task RefreshInternalAsync(
        CancellationToken cancellationToken)
    {
        var refreshed = (await this.entityRepository.ExportEntitySnapshotsAsync(cancellationToken))
            .ToDictionary(static pair => pair.Key, static pair => pair.Value);

        var changedEntityIds = new HashSet<EntityId>();
        foreach (var previous in this.snapshotsById)
        {
            if (!refreshed.TryGetValue(previous.Key, out var current))
            {
                changedEntityIds.Add(previous.Key);
                continue;
            }

            if (current.ModifiedTime.ChangeId != previous.Value.ModifiedTime.ChangeId)
            {
                changedEntityIds.Add(previous.Key);
            }
        }

        foreach (var current in refreshed.Keys)
        {
            if (!this.snapshotsById.ContainsKey(current))
            {
                changedEntityIds.Add(current);
            }
        }

        this.snapshotsById = refreshed;
        if (changedEntityIds.Count == 0)
        {
            return;
        }

        this.Changed?.Invoke(
            this,
            new EntityBrokerChangedEventArgs
            {
                ChangedEntityIds = changedEntityIds.ToArray(),
            });
    }
}

public sealed class EntityBrokerChangedEventArgs : EventArgs
{
    public required IReadOnlyCollection<EntityId> ChangedEntityIds { get; init; }
}
