using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Tests;

public sealed class WorkspacesTransportCompositionTests
{
    private static readonly EntityId LocalProfileId = new("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Composition_TransportFactoryRegistry_BuildsLocalTransportFactory()
    {
        await using var composition = CreateComposition();

        using var descriptor = JsonDocument.Parse("""{"type":"local"}""");
        var transport = await composition.TransportFactoryRegistry.ConnectToAsync(descriptor.RootElement, Ct());

        Assert.NotNull(transport);
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task Composition_TransportFactoryRegistry_BuildsUserComputerProfileTransportFactory()
    {
        await using var composition = CreateComposition();

        // The session's own profile id resolves to a local descriptor, so the user-computer-profile
        // factory routes back through the registry to the LocalTransportFactory: proves both factories
        // are registered and built in the composition.
        using var descriptor = JsonDocument.Parse(
            """{"type":"user-computer-profile","entity-id":"11111111-1111-1111-1111-111111111111"}""");
        var transport = await composition.TransportFactoryRegistry.ConnectToAsync(descriptor.RootElement, Ct());

        Assert.NotNull(transport);
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task Composition_UnknownDescriptor_ThrowsTransportException()
    {
        await using var composition = CreateComposition();

        using var descriptor = JsonDocument.Parse("""{"type":"nonexistent-transport"}""");
        await Assert.ThrowsAsync<TransportException>(
            () => composition.TransportFactoryRegistry.ConnectToAsync(descriptor.RootElement, Ct()));
    }

    [Fact]
    public async Task Composition_ExposesTrustedExecutorAndHostSurfaces()
    {
        await using var composition = CreateComposition();

        Assert.True(composition.TrustedExecutor.CanExecute("some-target"));
        Assert.NotNull(composition.TransportHost);
        Assert.NotNull(composition.ConnectionStatusRegistry);
        Assert.NotNull(composition.LocalListeners);
        Assert.Empty(composition.HubFactories);
    }

    private static WorkspacesTransportComposition CreateComposition()
    {
        var dataAccessLayer = new EntityLookupDataAccessLayer(
            (LocalProfileId, """{"entity-id":"11111111-1111-1111-1111-111111111111"}"""));
        var session = new WorkspaceEntitySession
        {
            UserEntityId = new EntityId("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            ComputerEntityId = new EntityId("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            UserComputerProfileEntityId = LocalProfileId,
        };
        return new WorkspacesTransportComposition(dataAccessLayer, session);
    }

    private static CancellationToken Ct() => new CancellationTokenSource(System.TimeSpan.FromSeconds(10)).Token;

    private sealed class EntityLookupDataAccessLayer : IDataAccessLayer
    {
        private readonly System.Collections.Generic.Dictionary<EntityId, JsonElement> entities = [];

        public EntityLookupDataAccessLayer(params (EntityId EntityId, string Json)[] seedEntities)
        {
            foreach (var (entityId, json) in seedEntities)
            {
                this.entities[entityId] = JsonDocument.Parse(json).RootElement.Clone();
            }
        }

        public Task<GetResult> GetAsync(GetRequest request, CancellationToken cancellationToken = default)
        {
            var snapshots = request.Entities
                .Where(entity => entity.EntityId is not null && this.entities.ContainsKey(entity.EntityId.Value))
                .Select(entity => new EntitySnapshot
                {
                    EntityId = entity.EntityId!.Value,
                    ModifiedTime = new Timestamp(System.DateTimeOffset.UnixEpoch, "test"),
                    Data = this.entities[entity.EntityId.Value].Clone(),
                    Relationships = [],
                })
                .ToArray();
            return Task.FromResult(new GetResult
            {
                Batches =
                [
                    new TimestampedEntityBatch
                    {
                        Entities = snapshots,
                    },
                ],
            });
        }

        public Task<UpdateResult> UpdateAsync(UpdateRequest request, CancellationToken cancellationToken = default)
            => throw new System.NotSupportedException();

        public Task<QueryResult> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
            => throw new System.NotSupportedException();

        public Task<GetHistoryResult> GetHistoryAsync(GetHistoryRequest request, CancellationToken cancellationToken = default)
            => throw new System.NotSupportedException();

        public Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken = default)
            => throw new System.NotSupportedException();

        public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(GetChangedEntitiesRequest request, CancellationToken cancellationToken = default)
            => throw new System.NotSupportedException();
    }
}
