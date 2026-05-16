using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Data.Tests;

namespace Phantom.Workspaces.Data.Offline.Tests;

public sealed class InMemoryDataAccessLayerTests : DataAccessLayerNonQueryTests
{
    protected override IDataAccessLayer CreateDataAccessLayer()
    {
        return new InMemoryDataAccessLayer();
    }
}
