using System;
using System.Collections.Generic;
using System.Text;

namespace Phantom.Workspaces.Data
{
    /// <summary>
    /// Performs referential integrity validation on data being updated on an underlying IDataAccessLayer.
    /// </summary>
    /// <remarks>
    /// This data access layer manages the "reference" relationships on entities. It ensures that when
    /// an entity is updated with a reference to another entity, the referenced entity actually exists,
    /// and adds the "reference" relationship to the data store. If an entity is deleted,
    /// all the relationships for that entity are also deleted. All the properties that are entity-id
    /// references are removed from the referring entities; if this causes a schema validation error,
    /// the UpdateResult will contain such validation error.
    /// </remarks>
    public class ReferentialIntegrityDataAccessLayer : IDataAccessLayer
    {
        public ReferentialIntegrityDataAccessLayer(
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
            // TODO: Do referential integrity validation here.
            return this.UnderlyingDataAccessLayer.UpdateAsync(request, cancellationToken);
        }
    }
}
