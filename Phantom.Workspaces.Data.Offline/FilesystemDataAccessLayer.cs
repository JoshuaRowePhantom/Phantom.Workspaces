using System;
using System.Collections.Generic;
using System.Text;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Data.Offline
{
    /// <summary>
    /// This IDataAccessLayer implementation uses the filesystem as its underlying data store.
    /// </summary>
    public class FilesystemDataAccessLayer : IDataAccessLayer
    {
        public FilesystemDataAccessLayer(
            string path)
        {
            this.Path = path;
        }
        
        public string Path { get; }

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
