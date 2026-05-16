using System;
using System.Collections.Generic;
using System.Text;

namespace Phantom.Workspaces.Data
{
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
    public class MergeProcessingDataAccessLayer : IDataAccessLayer
    {
        public MergeProcessingDataAccessLayer(
            IDataAccessLayer underlyingDataAccessLayer)
        {
            this.UnderlyingDataAccessLayer = underlyingDataAccessLayer;
        }

        IDataAccessLayer UnderlyingDataAccessLayer { get; }

        public Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken = default)
        {
            return this.UnderlyingDataAccessLayer.ExportAsync(request, cancellationToken);
        }

        public Task<GetResult> GetAsync(GetRequest request, CancellationToken cancellationToken = default)
        {
            return this.UnderlyingDataAccessLayer.GetAsync(request, cancellationToken);
        }

        public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(GetChangedEntitiesRequest request, CancellationToken cancellationToken = default)
        {
            return this.UnderlyingDataAccessLayer.GetChangedEntitiesAsync(request, cancellationToken);
        }

        public Task<GetHistoryResult> GetHistoryAsync(GetHistoryRequest request, CancellationToken cancellationToken = default)
        {
            return this.UnderlyingDataAccessLayer.GetHistoryAsync(request, cancellationToken);
        }

        public Task<QueryResult> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
        {
            return this.UnderlyingDataAccessLayer.QueryAsync(request, cancellationToken);
        }

        public Task<UpdateResult> UpdateAsync(UpdateRequest request, CancellationToken cancellationToken = default)
        {
            // TODO: Do merge processing here.
            throw new NotImplementedException();
        }
    }
}
