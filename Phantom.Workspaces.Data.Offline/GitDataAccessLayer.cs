using System;
using System.Collections.Generic;
using System.Text;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Data.Offline
{
    /// <summary>
    /// This IDataAccessLayer implementation uses Git as its underlying data store. 
    /// It is expected that the Git repository will be stored on the filesystem, 
    /// and that the GitDataAccessLayer will use the filesystem to access the Git repository. 
    /// The GitDataAccessLayer will use Git commands to perform the necessary operations on the Git repository,
    /// except when working on the latest snapshot, where it will use an underlying FilesystemDataAccessLayer.
    /// </summary>
    /// <remarks>
    /// Each git operation is atomic. This is done by:
    /// 
    /// ... do file modifications ...
    /// git add .
    /// git commit -m "commit message"
    /// git push origin main
    /// 
    /// If the git push is rejected, the process is retried until it succeeds, with an additional:
    ///
    /// git reset --hard HEAD origin/main
    /// 
    /// </remarks>
    public class GitDataAccessLayer : IDataAccessLayer
    {
        private FilesystemDataAccessLayer _filesystemDataAccessLayer;

        GitDataAccessLayer(
            FilesystemDataAccessLayer filesystemDataAccessLayer)
        {
            _filesystemDataAccessLayer = filesystemDataAccessLayer;
        }

        public string Path => _filesystemDataAccessLayer.Path;

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
