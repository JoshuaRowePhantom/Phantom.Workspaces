namespace Phantom.Workspaces.Data;

public static class ValidatedDataAccessLayerFactory
{
    public static IDataAccessLayer Create(IDataAccessLayer underlyingDataAccessLayer)
    {
        return Create(underlyingDataAccessLayer, validationPassStarted: null);
    }

    internal static IDataAccessLayer Create(
        IDataAccessLayer underlyingDataAccessLayer,
        Action<SchemaValidationPass>? validationPassStarted)
    {
        ArgumentNullException.ThrowIfNull(underlyingDataAccessLayer);

        var schemaAccessor = new SchemaAccessor(underlyingDataAccessLayer);
        return new MergeProcessingDataAccessLayer(
            new ReferentialIntegrityDataAccessLayer(
                underlyingDataAccessLayer,
                schemaAccessor,
                validationPassStarted));
    }
}

internal enum SchemaValidationPassKind
{
    ExternalUpdate,
    TrustedEmbeddedSeed,
}

internal readonly record struct SchemaValidationPass(
    SchemaValidationPassKind Kind,
    int ChangeCount);
