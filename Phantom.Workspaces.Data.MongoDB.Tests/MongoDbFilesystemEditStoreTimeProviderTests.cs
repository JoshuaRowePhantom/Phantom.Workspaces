using Microsoft.Extensions.Time.Testing;
using MongoDB.Driver;
using Moq;
using Phantom.Workspaces.Data.MongoDB;

namespace Phantom.Workspaces.Data.MongoDB.Tests;

/// <summary>
/// Unit tests proving <see cref="MongoDbFilesystemEditStore"/> stamps <c>CreatedAt</c> from the
/// injected <see cref="TimeProvider"/> rather than wall-clock. No Docker/MongoDB server required —
/// the collection is a Moq stub that captures the inserted document.
/// </summary>
public sealed class MongoDbFilesystemEditStoreTimeProviderTests
{
    private static (MongoDbFilesystemEditStore Store, Func<MongoDbFilesystemEditDocument?> GetInserted) CreateStore(
        TimeProvider timeProvider)
    {
        MongoDbFilesystemEditDocument? inserted = null;
        var collection = new Mock<IMongoCollection<MongoDbFilesystemEditDocument>>(MockBehavior.Loose);
        collection
            .Setup(c => c.InsertOneAsync(
                It.IsAny<MongoDbFilesystemEditDocument>(),
                It.IsAny<InsertOneOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<MongoDbFilesystemEditDocument, InsertOneOptions, CancellationToken>((doc, _, _) => inserted = doc)
            .Returns(Task.CompletedTask);

        var store = new MongoDbFilesystemEditStore(collection.Object, timeProvider);
        return (store, () => inserted);
    }

    [Fact]
    public async Task StoreEditAsync_StampsCreatedAtFromTimeProvider()
    {
        var instant = new DateTimeOffset(2024, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(instant);
        var (store, getInserted) = CreateStore(timeProvider);

        await store.StoreEditAsync("/path", "old", "new", preview: false, operation: "edit", CancellationToken.None);

        var inserted = getInserted();
        Assert.NotNull(inserted);
        Assert.Equal(instant.UtcDateTime, inserted!.CreatedAt);
    }

    [Fact]
    public async Task StoreEditAsync_AfterAdvance_StampsCreatedAtFromAdvancedTime()
    {
        var start = new DateTimeOffset(2024, 3, 4, 0, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(start);
        var (store, getInserted) = CreateStore(timeProvider);

        timeProvider.Advance(TimeSpan.FromHours(3));

        await store.StoreEditAsync("/path", "old", "new", preview: false, operation: "edit", CancellationToken.None);

        var inserted = getInserted();
        Assert.NotNull(inserted);
        Assert.Equal(start.UtcDateTime.AddHours(3), inserted!.CreatedAt);
    }
}
