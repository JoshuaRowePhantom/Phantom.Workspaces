using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Data.Tests;

public sealed class BaseUpdateProcessingDataAccessLayerTests
{
    [Fact]
    public async Task GetAsync_PassesRequestThroughUnchanged()
    {
        var request = new GetRequest(
            new[] { new EntityId(Guid.Parse("5d1d7fa5-7a74-4b0d-8aef-6b6e8d8f9d8d")) },
            null,
            null,
            new Timestamp?[] { null });
        var underlyingDataAccessLayer = new RecordingDataAccessLayer();
        var dataAccessLayer = new TestBaseUpdateProcessingDataAccessLayer(underlyingDataAccessLayer);

        await dataAccessLayer.GetAsync(request);

        Assert.Same(request, underlyingDataAccessLayer.GetRequest);
    }

    private sealed class RecordingDataAccessLayer : IDataAccessLayer
    {
        public GetRequest? GetRequest { get; private set; }

        public Task<ExportResult> ExportAsync(
            ExportRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ExportResult(Array.Empty<ExportChangeBatch>(), new Timestamp(DateTimeOffset.UnixEpoch, "0")));
        }

        public Task<GetResult> GetAsync(
            GetRequest request,
            CancellationToken cancellationToken = default)
        {
            this.GetRequest = request;
            return Task.FromResult(new GetResult(Array.Empty<TimestampedEntityBatch>()));
        }

        public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(
            GetChangedEntitiesRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new GetChangedEntitiesResult(Array.Empty<ChangedEntitySnapshot>()));
        }

        public Task<GetHistoryResult> GetHistoryAsync(
            GetHistoryRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new GetHistoryResult(Array.Empty<EntityHistoryEntry>()));
        }

        public Task<QueryResult> QueryAsync(
            QueryRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new QueryResult(Array.Empty<TimestampedQueryBatch>()));
        }

        public Task<UpdateResult> UpdateAsync(
            UpdateRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new UpdateResult(Array.Empty<EntityUpdateResult>()));
        }
    }

    private sealed class TestBaseUpdateProcessingDataAccessLayer : BaseUpdateProcessingDataAccessLayer
    {
        public TestBaseUpdateProcessingDataAccessLayer(
            IDataAccessLayer underlyingDataAccessLayer)
            : base(underlyingDataAccessLayer)
        {
        }
    }
}
