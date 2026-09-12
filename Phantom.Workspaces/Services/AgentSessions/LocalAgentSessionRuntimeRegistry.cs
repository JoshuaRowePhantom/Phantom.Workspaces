using System.Text.Json;
using AgentSchema;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Services.AgentSessions;

internal interface ILocalAgentSessionRuntimeRegistry
{
    Task SetContinueInBackgroundAsync(
        AgentSessionId sessionId,
        JsonElement persistedEntity,
        bool continueInBackground,
        CancellationToken ct);
}

internal sealed class LocalAgentSessionRuntimeRegistry(IDataAccessLayer dataAccessLayer)
    : ILocalAgentSessionRuntimeRegistry
{
    public async Task SetContinueInBackgroundAsync(
        AgentSessionId sessionId,
        JsonElement persistedEntity,
        bool continueInBackground,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!persistedEntity.TryGetProperty("entity-id", out var entityIdValue)
            || entityIdValue.ValueKind != JsonValueKind.String
            || !Guid.TryParse(entityIdValue.GetString(), out var entityId))
            throw new InvalidOperationException("The running session has no persisted entity identity.");

        var entity = await dataAccessLayer.GetAsync(new GetRequest
        {
            Entities = [new GetEntityRequest { EntityId = new EntityId(entityId) }],
            Timestamps = [null],
        }, ct).ConfigureAwait(false);
        var snapshot = entity.Batches.SelectMany(batch => batch.Entities).SingleOrDefault()
            ?? throw new InvalidOperationException($"Agent session '{sessionId.Value}' no longer exists.");
        if (snapshot.Data is not JsonElement current)
            throw new InvalidOperationException($"Agent session '{sessionId.Value}' has no persisted data.");

        var values = current.EnumerateObject().ToDictionary(
            property => property.Name,
            property => (object?)property.Value.Clone(),
            StringComparer.Ordinal);
        values["continue-in-background"] = continueInBackground;
        var result = await dataAccessLayer.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata
            {
                Comment = new Markdown { Text = "Update agent session background preference" },
            },
            Changes =
            [
                new EntityChange
                {
                    EntityId = snapshot.EntityId,
                    ConcurrencyTag = snapshot.ConcurrencyTag,
                    Data = JsonSerializer.SerializeToElement(values),
                    EntityChangeMode = EntityChangeMode.Replace,
                },
            ],
        }, ct).ConfigureAwait(false);
        var change = result.EntityResults.SingleOrDefault();
        if (change is null
            || change.Errors.Count != 0
            || change.ConcurrencyMatchState == ConcurrencyMatchState.NotMatched)
            throw new InvalidOperationException("The background preference could not be persisted.");
    }
}
