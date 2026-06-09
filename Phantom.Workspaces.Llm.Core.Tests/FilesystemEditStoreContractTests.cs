using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Llm.Tests;

public abstract class FilesystemEditStoreContractTests
{
    protected abstract ValueTask<IFilesystemEditStore> CreateStoreAsync();

    protected virtual ValueTask ResetStoreAsync()
    {
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task StoreEditAsync_ThenGetEditAsync_RoundTripsStoredFields()
    {
        await this.ResetStoreAsync();
        var store = await this.CreateStoreAsync();
        var beforeStore = DateTime.UtcNow.AddMinutes(-1);

        var editId = await store.StoreEditAsync(
            path: "contract-path.txt",
            originalContent: "before",
            modifiedContent: "after",
            preview: true,
            operation: "replace",
            cancellationToken: CancellationToken.None);
        var stored = await store.GetEditAsync(editId, CancellationToken.None);

        Assert.NotNull(stored);
        Assert.Equal(editId, stored!.Id);
        Assert.Equal("contract-path.txt", stored.Path);
        Assert.Equal("before", stored.OriginalContent);
        Assert.Equal("after", stored.ModifiedContent);
        Assert.True(stored.Preview);
        Assert.Equal("replace", stored.Operation);
        Assert.True(stored.CreatedAt >= beforeStore);
    }

    [Fact]
    public async Task GetEditAsync_WhenEditDoesNotExist_ReturnsNull()
    {
        await this.ResetStoreAsync();
        var store = await this.CreateStoreAsync();

        var stored = await store.GetEditAsync("does-not-exist", CancellationToken.None);

        Assert.Null(stored);
    }

    [Fact]
    public async Task StoreEditAsync_WhenCalledTwice_ReturnsDistinctEditIds()
    {
        await this.ResetStoreAsync();
        var store = await this.CreateStoreAsync();

        var firstEditId = await store.StoreEditAsync(
            path: "first.txt",
            originalContent: "one",
            modifiedContent: "two",
            preview: false,
            operation: "replace",
            cancellationToken: CancellationToken.None);
        var secondEditId = await store.StoreEditAsync(
            path: "second.txt",
            originalContent: "alpha",
            modifiedContent: "beta",
            preview: false,
            operation: "replace",
            cancellationToken: CancellationToken.None);

        Assert.NotEqual(firstEditId, secondEditId);
    }
}
