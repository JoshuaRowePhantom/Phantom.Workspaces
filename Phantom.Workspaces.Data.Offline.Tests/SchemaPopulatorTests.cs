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

    private sealed class CountingDataAccessLayer : BaseUpdateProcessingDataAccessLayer
    {
        public CountingDataAccessLayer(
            IDataAccessLayer inner)
            : base(inner)
        {
        }

        public int UpdateCallCount { get; private set; }

        public override Task<UpdateResult> UpdateAsync(
            UpdateRequest request,
            CancellationToken cancellationToken = default)
        {
            this.UpdateCallCount++;
            return base.UpdateAsync(request, cancellationToken);
        }
    }
}
