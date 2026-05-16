namespace Phantom.Workspaces.Data;

/// <summary>
/// An UpdateProcessingDataAccessLayer translates the UpdateAsync calls
/// into a series of GetAsync and UpdateAsync calls on an underlying IDataAccessLayer, to perform the necessary processing
/// to merge the updates with the existing data.
/// The underlying IDataAccessLayer is expected to perform schema validation and referential integrity validation,
/// so the UpdateProcessingDataAccessLayer does not need to worry about such validations.
/// The underlying IDataAccessLayer can assume every EntityChange.Data will represent a complete
/// set of data for the entity, and that MergeMode will be Replace for each change.
/// If a merge requires getting the entity data, and the get returns a different concurrency key,
/// then a concurrency conflict is detected and will be thrown.
/// </summary>
public class MergeProcessingDataAccessLayer : BaseUpdateProcessingDataAccessLayer
{
    public MergeProcessingDataAccessLayer(
        IDataAccessLayer underlyingDataAccessLayer)
        : base(underlyingDataAccessLayer)
    {
    }

    public override Task<UpdateResult> UpdateAsync(
        UpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        // TODO: Do merge processing here.
        throw new NotImplementedException();
    }
}
