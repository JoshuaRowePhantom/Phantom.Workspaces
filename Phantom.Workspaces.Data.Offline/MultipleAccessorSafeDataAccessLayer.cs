using System;
using System.Collections.Generic;
using System.Text;

namespace Phantom.Workspaces.Data.Offline
{
    /// <summary>
    /// This IDataAccessLayer implementation applies thread and process safe measures
    /// to accessing an underlying IDataAccessLayer.
    /// </summary>
    class MultipleAccessorSafeDataAccessLayer :
        IDataAccessLayer
    {
        public Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<GetResult> GetAsync(GetRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(GetChangedEntitiesRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<GetHistoryResult> GetHistoryAsync(GetHistoryRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<QueryResult> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<UpdateResult> UpdateAsync(UpdateRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }
}
