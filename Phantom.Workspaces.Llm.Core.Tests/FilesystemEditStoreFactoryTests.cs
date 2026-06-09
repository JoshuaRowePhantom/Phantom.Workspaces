using System.Text.Json;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class FilesystemEditStoreFactoryTests
{
    [Fact]
    public async Task CreateAsync_WhenConnectionJsonMissing_ReturnsInMemoryStore()
    {
        var store = await FilesystemEditStoreFactory.CreateAsync(connectionJson: null, CancellationToken.None);

        Assert.IsType<InMemoryFilesystemEditStore>(store);
    }

    [Fact]
    public async Task CreateAsync_WhenConnectionJsonWhitespace_ReturnsInMemoryStore()
    {
        var store = await FilesystemEditStoreFactory.CreateAsync(connectionJson: "   ", CancellationToken.None);

        Assert.IsType<InMemoryFilesystemEditStore>(store);
    }

    [Fact]
    public async Task CreateAsync_WhenConnectionJsonIsInvalid_Throws()
    {
        await Assert.ThrowsAsync<JsonException>(async () =>
        {
            await FilesystemEditStoreFactory.CreateAsync("{\"provider\":\"unknown\"}", CancellationToken.None);
        });
    }
}
