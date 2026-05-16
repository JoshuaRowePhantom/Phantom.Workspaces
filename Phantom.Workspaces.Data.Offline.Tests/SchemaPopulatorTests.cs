using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;

namespace Phantom.Workspaces.Data.Offline.Tests;

public sealed class SchemaPopulatorTests
{
    [Fact]
    public async Task Populate_LoadsEmbeddedEntities_IntoInMemoryStore()
    {
        var inMemoryDataAccessLayer = new InMemoryDataAccessLayer();
        var countingDataAccessLayer = new CountingDataAccessLayer(inMemoryDataAccessLayer);
        var schemaPopulator = new SchemaPopulator(countingDataAccessLayer);

        var errors = await schemaPopulator.Populate();

        Assert.Equal(1, countingDataAccessLayer.UpdateCallCount);
        Assert.True(
            errors.Count == 0,
            string.Join(
                Environment.NewLine,
                errors.Select(
                    error => $"{error.RelatedEntityId?.Value}: {error.Message}")));
    }

    private sealed class CountingDataAccessLayer : IDataAccessLayer
    {
        private readonly IDataAccessLayer inner;

        public CountingDataAccessLayer(
            IDataAccessLayer inner)
        {
            this.inner = inner;
        }

        public int UpdateCallCount { get; private set; }

        public Task<ExportResult> ExportAsync(
            ExportRequest request,
            CancellationToken cancellationToken = default)
        {
            return this.inner.ExportAsync(request, cancellationToken);
        }

        public Task<GetResult> GetAsync(
            GetRequest request,
            CancellationToken cancellationToken = default)
        {
            return this.inner.GetAsync(request, cancellationToken);
        }

        public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(
            GetChangedEntitiesRequest request,
            CancellationToken cancellationToken = default)
        {
            return this.inner.GetChangedEntitiesAsync(request, cancellationToken);
        }

        public Task<GetHistoryResult> GetHistoryAsync(
            GetHistoryRequest request,
            CancellationToken cancellationToken = default)
        {
            return this.inner.GetHistoryAsync(request, cancellationToken);
        }

        public Task<QueryResult> QueryAsync(
            QueryRequest request,
            CancellationToken cancellationToken = default)
        {
            return this.inner.QueryAsync(request, cancellationToken);
        }

        public Task<UpdateResult> UpdateAsync(
            UpdateRequest request,
            CancellationToken cancellationToken = default)
        {
            this.UpdateCallCount++;
            return this.inner.UpdateAsync(request, cancellationToken);
        }
    }
}
