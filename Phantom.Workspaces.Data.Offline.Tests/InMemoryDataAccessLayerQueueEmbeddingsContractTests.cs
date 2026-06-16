using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Tests;

namespace Phantom.Workspaces.Data.Offline.Tests;

public sealed class InMemoryDataAccessLayerQueueEmbeddingsContractTests : DataAccessLayerQueueEmbeddingsContractTests
{
    protected override IDataAccessLayer CreateDataAccessLayer()
    {
        return new InMemoryDataAccessLayer();
    }
}
