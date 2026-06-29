using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Services;

public interface IAgentPersistenceStoreCache
{
    Task<IAgentPersistenceStore> GetOrCreateAsync(RepositorySource repositorySource);
}
