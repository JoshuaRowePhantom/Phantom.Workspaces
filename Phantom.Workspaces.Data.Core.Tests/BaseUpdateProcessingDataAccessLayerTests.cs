using Phantom.Workspaces.Data;

#pragma warning disable CS0618
namespace Phantom.Workspaces.Data.Tests;

public sealed class BaseUpdateProcessingDataAccessLayerTests
{
    [Fact]
    public async Task GetAsync_PassesRequestThroughUnchanged()
    {
        var request = new GetRequest
        {
            Entities =
            [
                new GetEntityRequest
                {
                    EntityId = new EntityId("5d1d7fa5-7a74-4b0d-8aef-6b6e8d8f9d8d"),
                },
            ],
            Timestamps = new Timestamp?[] { null },
        };
        var underlyingDataAccessLayer = new RecordingDataAccessLayer();
        var dataAccessLayer = new TestBaseUpdateProcessingDataAccessLayer(underlyingDataAccessLayer);

        await dataAccessLayer.GetAsync(request);

        Assert.Same(request, underlyingDataAccessLayer.GetRequest);
    }
    #pragma warning restore CS0618

    [Fact]
    public async Task ExportAsync_PassesRequestThroughUnchanged()
    {
        var request = new ExportRequest
        {
            SnapshotTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
        };
        var underlyingDataAccessLayer = new RecordingDataAccessLayer();
        var dataAccessLayer = new TestBaseUpdateProcessingDataAccessLayer(underlyingDataAccessLayer);

#pragma warning disable CS0618
        var result = await dataAccessLayer.ExportAsync(request);
#pragma warning restore CS0618

        Assert.Same(request, underlyingDataAccessLayer.ExportRequest);
        Assert.Same(underlyingDataAccessLayer.ExportResultToReturn, result);
    }

    [Fact]
    public async Task GetChangedEntitiesAsync_PassesRequestThroughUnchanged()
    {
        var request = new GetChangedEntitiesRequest
        {
            EntityIdTimestamps =
            [
                new EntityIdTimestamp(
                    new EntityId("3de4f96b-b40a-45ab-b7ec-43c33188bc0f"),
                    new Timestamp(DateTimeOffset.UtcNow, "2")),
            ],
        };
        var underlyingDataAccessLayer = new RecordingDataAccessLayer();
        var dataAccessLayer = new TestBaseUpdateProcessingDataAccessLayer(underlyingDataAccessLayer);

        var result = await dataAccessLayer.GetChangedEntitiesAsync(request);

        Assert.Same(request, underlyingDataAccessLayer.GetChangedEntitiesRequest);
        Assert.Same(underlyingDataAccessLayer.GetChangedEntitiesResultToReturn, result);
    }

    [Fact]
    public async Task GetHistoryAsync_PassesRequestThroughUnchanged()
    {
        var request = new GetHistoryRequest
        {
            EntityIds =
            [
                new EntityId("6eb7709a-ddce-4c45-8dfd-cc3d3f2d536f"),
            ],
        };
        var underlyingDataAccessLayer = new RecordingDataAccessLayer();
        var dataAccessLayer = new TestBaseUpdateProcessingDataAccessLayer(underlyingDataAccessLayer);

        var result = await dataAccessLayer.GetHistoryAsync(request);

        Assert.Same(request, underlyingDataAccessLayer.GetHistoryRequest);
        Assert.Same(underlyingDataAccessLayer.GetHistoryResultToReturn, result);
    }

    [Fact]
    public async Task QueryAsync_PassesRequestThroughUnchanged()
    {
        var request = new QueryRequest
        {
            Clauses =
            [
                new TopLevelQueryClause
                {
                    ClauseIdentifier = new QueryClauseIdentifier("query-clause"),
                    Clause = new EntityTypeQueryClause
                    {
                        EntityTypeNames = new EntityTypeNameSet(["entity"]),
                    },
                },
            ],
            Timestamps = new Timestamp?[] { null },
        };
        var underlyingDataAccessLayer = new RecordingDataAccessLayer();
        var dataAccessLayer = new TestBaseUpdateProcessingDataAccessLayer(underlyingDataAccessLayer);

        var result = await dataAccessLayer.QueryAsync(request);

        Assert.Same(request, underlyingDataAccessLayer.QueryRequest);
        Assert.Same(underlyingDataAccessLayer.QueryResultToReturn, result);
    }

    [Fact]
    public async Task UpdateAsync_PassesRequestThroughUnchanged()
    {
        var request = new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata
            {
                Comment = new Markdown
                {
                    Text = "update",
                },
            },
            Changes =
            [
                new EntityChange
                {
                    EntityId = new EntityId("8657bc37-f020-4d89-b93d-c7bd1d76ee58"),
                    EntityChangeMode = EntityChangeMode.Replace,
                },
            ],
        };
        var underlyingDataAccessLayer = new RecordingDataAccessLayer();
        var dataAccessLayer = new TestBaseUpdateProcessingDataAccessLayer(underlyingDataAccessLayer);

        var result = await dataAccessLayer.UpdateAsync(request);

        Assert.Same(request, underlyingDataAccessLayer.UpdateRequest);
        Assert.Same(underlyingDataAccessLayer.UpdateResultToReturn, result);
    }

    private sealed class RecordingDataAccessLayer : IDataAccessLayer
    {
        public ExportRequest? ExportRequest { get; private set; }

        public GetRequest? GetRequest { get; private set; }

        public GetChangedEntitiesRequest? GetChangedEntitiesRequest { get; private set; }

        public GetHistoryRequest? GetHistoryRequest { get; private set; }

        public QueryRequest? QueryRequest { get; private set; }

        public UpdateRequest? UpdateRequest { get; private set; }

        public ExportResult ExportResultToReturn { get; } = new()
        {
            ChangeBatches = Array.Empty<ExportChangeBatch>(),
            FinalSnapshotTime = new Timestamp(DateTimeOffset.UnixEpoch, "0"),
        };

        public GetResult GetResultToReturn { get; } = new()
        {
            Batches = Array.Empty<TimestampedEntityBatch>(),
        };

        public GetChangedEntitiesResult GetChangedEntitiesResultToReturn { get; } = new()
        {
            Entities = Array.Empty<ChangedEntitySnapshot>(),
        };

        public GetHistoryResult GetHistoryResultToReturn { get; } = new()
        {
            History = Array.Empty<EntityHistoryEntry>(),
        };

        public QueryResult QueryResultToReturn { get; } = new()
        {
            Batches = Array.Empty<TimestampedQueryBatch>(),
        };

        public UpdateResult UpdateResultToReturn { get; } = new()
        {
            EntityResults = Array.Empty<EntityUpdateResult>(),
        };

        public Task<ExportResult> ExportAsync(
            ExportRequest request,
            CancellationToken cancellationToken = default)
        {
            this.ExportRequest = request;
            return Task.FromResult(this.ExportResultToReturn);
        }

        public Task<GetResult> GetAsync(
            GetRequest request,
            CancellationToken cancellationToken = default)
        {
            this.GetRequest = request;
            return Task.FromResult(this.GetResultToReturn);
        }

        public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(
            GetChangedEntitiesRequest request,
            CancellationToken cancellationToken = default)
        {
            this.GetChangedEntitiesRequest = request;
            return Task.FromResult(this.GetChangedEntitiesResultToReturn);
        }

        public Task<GetHistoryResult> GetHistoryAsync(
            GetHistoryRequest request,
            CancellationToken cancellationToken = default)
        {
            this.GetHistoryRequest = request;
            return Task.FromResult(this.GetHistoryResultToReturn);
        }

        public Task<QueryResult> QueryAsync(
            QueryRequest request,
            CancellationToken cancellationToken = default)
        {
            this.QueryRequest = request;
            return Task.FromResult(this.QueryResultToReturn);
        }

        public Task<UpdateResult> UpdateAsync(
            UpdateRequest request,
            CancellationToken cancellationToken = default)
        {
            this.UpdateRequest = request;
            return Task.FromResult(this.UpdateResultToReturn);
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
