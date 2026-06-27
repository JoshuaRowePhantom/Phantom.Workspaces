using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ScheduledTools;

/// <summary>
/// Reads and writes the persisted <c>scheduled-tools-paused</c> flag on a host profile entity and
/// exposes the current pause state to the scheduler runtime and UI. The persisted "Stop all / Pause"
/// action sets the flag and requests cancellation of in-flight runs via the
/// <see cref="ScheduledToolHost"/> (see <c>docs/design/scheduled-tools.md</c>).
/// </summary>
public sealed class ScheduledToolPauseStateService
{
    private const string PausedPropertyName = "scheduled-tools-paused";

    private readonly IDataAccessLayer dataAccessLayer;
    private readonly ScheduledToolHost host;

    public ScheduledToolPauseStateService(IDataAccessLayer dataAccessLayer, ScheduledToolHost host)
    {
        this.dataAccessLayer = dataAccessLayer ?? throw new ArgumentNullException(nameof(dataAccessLayer));
        this.host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>Raised whenever the cached pause state changes.</summary>
    public event EventHandler? PauseStateChanged;

    /// <summary>The most recently observed persisted pause state.</summary>
    public bool IsPaused { get; private set; }

    /// <summary>Reads the persisted pause state from the host profile entity and caches it.</summary>
    public async Task<bool> RefreshAsync(EntityId hostEntityId, CancellationToken cancellationToken = default)
    {
        var data = (await this.ReadSnapshotAsync(hostEntityId, cancellationToken).ConfigureAwait(false))?.Data;
        var paused = data is { } hostData
            && hostData.TryGetProperty(PausedPropertyName, out var pausedElement)
            && pausedElement.ValueKind == JsonValueKind.True;

        this.SetCachedState(paused);
        return paused;
    }

    /// <summary>
    /// Persists <paramref name="paused"/> on the host profile entity. When pausing, also requests
    /// cancellation of all currently running scheduled tool executions (the "Stop all" behavior).
    /// </summary>
    public async Task SetPausedAsync(EntityId hostEntityId, bool paused, CancellationToken cancellationToken = default)
    {
        var snapshot = await this.ReadSnapshotAsync(hostEntityId, cancellationToken).ConfigureAwait(false);
        if (snapshot?.Data is not { } data)
        {
            throw new InvalidOperationException($"Host profile entity {hostEntityId} was not found.");
        }

        var node = JsonNode.Parse(data.GetRawText())!.AsObject();
        node[PausedPropertyName] = paused;
        var updated = JsonSerializer.SerializeToElement(node);

        var result = await this.dataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "scheduled-tools: set pause state" } },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = hostEntityId,
                        ConcurrencyTag = snapshot.ConcurrencyTag,
                        Data = updated,
                        EntityChangeMode = EntityChangeMode.Replace,
                    },
                ],
            },
            cancellationToken).ConfigureAwait(false);

        if (result.EntityResults.Any(entityResult => entityResult.UpdateState == UpdateState.Failed))
        {
            throw new InvalidOperationException($"Failed to persist scheduled-tools-paused for host {hostEntityId}.");
        }

        if (paused)
        {
            this.host.StopAllRunningExecutions();
        }

        this.SetCachedState(paused);
    }

    private void SetCachedState(bool paused)
    {
        if (this.IsPaused == paused)
        {
            return;
        }

        this.IsPaused = paused;
        this.PauseStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<EntitySnapshot?> ReadSnapshotAsync(EntityId entityId, CancellationToken cancellationToken)
    {
        var getResult = await this.dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities = [new GetEntityRequest { EntityId = entityId }],
                Timestamps = [null],
            },
            cancellationToken).ConfigureAwait(false);
        return getResult.Batches
            .SelectMany(batch => batch.Entities)
            .FirstOrDefault(snapshot => snapshot.EntityId == entityId);
    }
}
