using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Tests;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class InMemoryFilesystemEditStoreContractTests : FilesystemEditStoreContractTests
{
    protected override ValueTask<IFilesystemEditStore> CreateStoreAsync()
    {
        return ValueTask.FromResult<IFilesystemEditStore>(new InMemoryFilesystemEditStore());
    }

    [Fact]
    public async Task GetEditAsync_WhenIdWasNeverStored_ReturnsNull()
    {
        var store = new InMemoryFilesystemEditStore();

        var stored = await store.GetEditAsync("missing-edit-id", CancellationToken.None);

        Assert.Null(stored);
    }
}
