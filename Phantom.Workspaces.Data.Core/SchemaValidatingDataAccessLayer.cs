using System;
using System.Collections.Generic;
using System.Text;

namespace Phantom.Workspaces.Data
{
    /// <summary>
    /// Performs schema validation on data being updated on an underlying IDataAccessLayer.
    /// </summary>
    /// <remarks>
    /// This data access layer expects UpdateRequests to have had merge processing already performed.
    /// </remarks>
    public class SchemaValidatingDataAccessLayer : IDataAccessLayer
    {
        public SchemaValidatingDataAccessLayer(
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
            // TODO: Do schema validation here.
            return this.UnderlyingDataAccessLayer.UpdateAsync(request, cancellationToken);
        }
    }
}
