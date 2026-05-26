namespace Phantom.Workspaces.Data.Tests;

#pragma warning disable CS0618
public sealed class PerInvocationDataAccessLayerTests
{
    [Fact]
    public async Task AllMethods_CreateAndDisposeNewUnderlyingDataAccessLayerPerInvocation()
    {
        var tracker = new AsyncDisposableTracker();
        var dataAccessLayer = new PerInvocationDataAccessLayer(tracker.Create);

        await dataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "update",
                    },
                },
                Changes = Array.Empty<EntityChange>(),
            });

        await dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities = Array.Empty<GetEntityRequest>(),
            });

        await dataAccessLayer.QueryAsync(
            new QueryRequest
            {
                Clauses = Array.Empty<TopLevelQueryClause>(),
            });

        await dataAccessLayer.GetHistoryAsync(
            new GetHistoryRequest
            {
                EntityIds = Array.Empty<EntityId>(),
            });

        await dataAccessLayer.ExportAsync(new ExportRequest());

        await dataAccessLayer.GetChangedEntitiesAsync(
            new GetChangedEntitiesRequest
            {
                EntityIdTimestamps = Array.Empty<EntityIdTimestamp>(),
            });

        Assert.Equal(6, tracker.CreatedCount);
        Assert.Equal(6, tracker.AsyncDisposedCount);
        Assert.Equal(0, tracker.DisposedCount);
    }

    [Fact]
    public async Task UnderlyingFailure_StillDisposesCreatedDataAccessLayer()
    {
        var tracker = new AsyncDisposableTracker
        {
            ThrowOnUpdate = true,
        };
        var dataAccessLayer = new PerInvocationDataAccessLayer(tracker.Create);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => dataAccessLayer.UpdateAsync(
                new UpdateRequest
                {
                    UpdateMetadata = new UpdateMetadata
                    {
                        Comment = new Markdown
                        {
                            Text = "update",
                        },
                    },
                    Changes = Array.Empty<EntityChange>(),
                }));

        Assert.Equal(1, tracker.CreatedCount);
        Assert.Equal(1, tracker.AsyncDisposedCount);
        Assert.Equal(0, tracker.DisposedCount);
    }

    [Fact]
    public async Task UsesSynchronousDispose_WhenUnderlyingDoesNotSupportAsyncDispose()
    {
        var tracker = new SyncDisposableTracker();
        var dataAccessLayer = new PerInvocationDataAccessLayer(tracker.Create);

        await dataAccessLayer.GetHistoryAsync(
            new GetHistoryRequest
            {
                EntityIds = Array.Empty<EntityId>(),
            });

        Assert.Equal(1, tracker.CreatedCount);
        Assert.Equal(1, tracker.DisposedCount);
    }

    private sealed class AsyncDisposableTracker
    {
        public int CreatedCount { get; private set; }

        public int AsyncDisposedCount { get; private set; }

        public int DisposedCount { get; private set; }

        public bool ThrowOnUpdate { get; init; }

        public IDataAccessLayer Create()
        {
            this.CreatedCount++;
            return new AsyncDisposableStubDataAccessLayer(this);
        }

        private sealed class AsyncDisposableStubDataAccessLayer : IDataAccessLayer, IDisposable, IAsyncDisposable
        {
            private readonly AsyncDisposableTracker tracker;

            public AsyncDisposableStubDataAccessLayer(
                AsyncDisposableTracker tracker)
            {
                this.tracker = tracker;
            }

            public Task<UpdateResult> UpdateAsync(
                UpdateRequest request,
                CancellationToken cancellationToken = default)
            {
                if (this.tracker.ThrowOnUpdate)
                {
                    throw new InvalidOperationException("update failed");
                }

                return Task.FromResult(
                    new UpdateResult
                    {
                        EntityResults = Array.Empty<EntityUpdateResult>(),
                    });
            }

            public Task<GetResult> GetAsync(
                GetRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(
                    new GetResult
                    {
                        Batches = Array.Empty<TimestampedEntityBatch>(),
                    });
            }

            public Task<QueryResult> QueryAsync(
                QueryRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(
                    new QueryResult
                    {
                        Batches = Array.Empty<TimestampedQueryBatch>(),
                    });
            }

            public Task<GetHistoryResult> GetHistoryAsync(
                GetHistoryRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(
                    new GetHistoryResult
                    {
                        History = Array.Empty<EntityHistoryEntry>(),
                    });
            }

            public Task<ExportResult> ExportAsync(
                ExportRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(
                    new ExportResult
                    {
                        ChangeBatches = Array.Empty<ExportChangeBatch>(),
                        FinalSnapshotTime = new Timestamp(DateTimeOffset.UtcNow, "change"),
                    });
            }

            public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(
                GetChangedEntitiesRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(
                    new GetChangedEntitiesResult
                    {
                        Entities = Array.Empty<ChangedEntitySnapshot>(),
                    });
            }

            public void Dispose()
            {
                this.tracker.DisposedCount++;
            }

            public ValueTask DisposeAsync()
            {
                this.tracker.AsyncDisposedCount++;
                return ValueTask.CompletedTask;
            }
        }
        #pragma warning restore CS0618
    }

    private sealed class SyncDisposableTracker
    {
        public int CreatedCount { get; private set; }

        public int DisposedCount { get; private set; }

        public IDataAccessLayer Create()
        {
            this.CreatedCount++;
            return new SyncDisposableStubDataAccessLayer(this);
        }

        private sealed class SyncDisposableStubDataAccessLayer : IDataAccessLayer, IDisposable
        {
            private readonly SyncDisposableTracker tracker;

            public SyncDisposableStubDataAccessLayer(
                SyncDisposableTracker tracker)
            {
                this.tracker = tracker;
            }

            public Task<UpdateResult> UpdateAsync(
                UpdateRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(
                    new UpdateResult
                    {
                        EntityResults = Array.Empty<EntityUpdateResult>(),
                    });
            }

            public Task<GetResult> GetAsync(
                GetRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(
                    new GetResult
                    {
                        Batches = Array.Empty<TimestampedEntityBatch>(),
                    });
            }

            public Task<QueryResult> QueryAsync(
                QueryRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(
                    new QueryResult
                    {
                        Batches = Array.Empty<TimestampedQueryBatch>(),
                    });
            }

            public Task<GetHistoryResult> GetHistoryAsync(
                GetHistoryRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(
                    new GetHistoryResult
                    {
                        History = Array.Empty<EntityHistoryEntry>(),
                    });
            }

            public Task<ExportResult> ExportAsync(
                ExportRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(
                    new ExportResult
                    {
                        ChangeBatches = Array.Empty<ExportChangeBatch>(),
                        FinalSnapshotTime = new Timestamp(DateTimeOffset.UtcNow, "change"),
                    });
            }

            public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(
                GetChangedEntitiesRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(
                    new GetChangedEntitiesResult
                    {
                        Entities = Array.Empty<ChangedEntitySnapshot>(),
                    });
            }

            public void Dispose()
            {
                this.tracker.DisposedCount++;
            }
        }
    }
}
