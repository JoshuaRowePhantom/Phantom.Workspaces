using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;

namespace Phantom.Workspaces.Data.Web.Server;

public static class WebServerDataAccessLayerFactory
{
    public static async Task<IDataAccessLayer> CreateDefaultAsync(CancellationToken cancellationToken = default)
    {
        var dataAccessLayer = new MergeProcessingDataAccessLayer(
            new ReferentialIntegrityDataAccessLayer(
                new SchemaValidatingDataAccessLayer(
                    new InMemoryDataAccessLayer())));

        var errors = await new SchemaPopulator(dataAccessLayer).Populate().ConfigureAwait(false);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Failed to populate web server schemas: {string.Join(" | ", errors.Select(static error => error.Message))}");
        }

        return dataAccessLayer;
    }
}
