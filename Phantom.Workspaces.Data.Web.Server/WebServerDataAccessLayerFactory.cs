using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;

namespace Phantom.Workspaces.Data.Web.Server;

public static class WebServerDataAccessLayerFactory
{
    public static async Task<IDataAccessLayer> CreateDefaultAsync(CancellationToken cancellationToken = default)
    {
        return await CreateDefaultAsync(validationPassStarted: null, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<IDataAccessLayer> CreateDefaultAsync(
        Action<SchemaValidationPass>? validationPassStarted,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dataAccessLayer = ValidatedDataAccessLayerFactory.Create(
            new InMemoryDataAccessLayer(),
            validationPassStarted);

        var errors = await new SchemaPopulator(dataAccessLayer).PopulateAsync(cancellationToken).ConfigureAwait(false);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Failed to populate web server schemas: {string.Join(" | ", errors.Select(static error => error.Message))}");
        }

        return dataAccessLayer;
    }
}
