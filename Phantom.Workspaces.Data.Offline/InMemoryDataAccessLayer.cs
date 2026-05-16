using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;

namespace Phantom.Workspaces.Data
{
    public class InMemoryDataAccessLayer : IDataAccessLayer
    {
        class Entity
        {
            public EntityId EntityId { get; }

            public ConcurrencyTag ConcurrencyTag { get; }

            public JsonNode JsonNode { get; }

            public ImmutableSortedSet<StateSnapshot> History { get; }

            public Entity(
                EntityId entityId,
                ConcurrencyTag concurrencyTag,
                JsonNode jsonNode)
            {
                this.EntityId = entityId;
                this.ConcurrencyTag = concurrencyTag;
                this.JsonNode = jsonNode;
                this.History = ImmutableSortedSet<StateSnapshot>.Empty;
            }

            public Entity(
                Entity previousEntity,
                ConcurrencyTag concurrencyTag,
                JsonNode newJsonNode,
                StateSnapshot newStateSnapshot)
            {
                this.EntityId = previousEntity.EntityId;
                this.ConcurrencyTag = concurrencyTag;
                this.JsonNode = newJsonNode;
                this.History = previousEntity.History.Add(newStateSnapshot);
            }
        }

        class StateSnapshot
        {
            public DateTimeOffset Timestamp { get; }
            public int Version { get; }
            public UpdateMetadata UpdateMetadata { get; }

            public ImmutableHashSet<Entity> Entities { get; }

            public StateSnapshot()
            {
                this.Timestamp = DateTimeOffset.UtcNow;
                this.Version = 0;
                this.UpdateMetadata = new UpdateMetadata(new Markdown("Empty repository created."));
                this.Entities = ImmutableHashSet<Entity>.Empty;
            }
        }

        class State
        {
            public StateSnapshot Current { get; } = new StateSnapshot();

            public ImmutableSortedSet<StateSnapshot> StateSnapshots { get; }

            public State() 
            {
                Current = new StateSnapshot();
                StateSnapshots = ImmutableSortedSet.Create(Current);
            }

            public State(
                State previousState,
                StateSnapshot newStateSnapshot)
            {
                Current = newStateSnapshot;
                StateSnapshots = previousState.StateSnapshots.Add(newStateSnapshot);
            }
        }

        State _state = new State();

        State AtomicallyReplaceState(State previousState, State newState)
        {
            return Interlocked.CompareExchange(ref _state, newState, previousState);
        }

        public Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<GetResult> GetAsync(GetRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(GetChangedEntitiesRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<GetHistoryResult> GetHistoryAsync(GetHistoryRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<QueryResult> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<UpdateResult> UpdateAsync(UpdateRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }
}
