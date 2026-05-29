using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Tests;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class InMemoryAgentPersistenceStoreContractTests : AgentPersistenceStoreContractTests
{
    protected override ValueTask<IAgentPersistenceStore> CreateStoreAsync()
    {
        return ValueTask.FromResult<IAgentPersistenceStore>(new InMemoryAgentPersistenceStore());
    }
}
