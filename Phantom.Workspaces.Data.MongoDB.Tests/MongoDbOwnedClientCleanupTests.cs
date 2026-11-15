using MongoDB.Driver;
using Moq;

namespace Phantom.Workspaces.Data.MongoDB.Tests;

public sealed class MongoDbOwnedClientCleanupTests
{
    [Fact]
    public async Task FailedDatabaseAcquisition_StillDropsPrivateNameAndDisposesOwnedClient()
    {
        var name = "pw_mongodb_test_" + Guid.NewGuid().ToString("N");
        var failure = new IOException("database acquisition failed");
        var client = new Mock<IMongoClient>(MockBehavior.Strict);
        client.Setup(value => value.GetDatabase(name, null)).Throws(failure);
        client.Setup(value => value.DropDatabaseAsync(name, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var disposable = client.As<IDisposable>();
        disposable.Setup(value => value.Dispose());
        var fixture = new MongoDbTestDatabaseFixture(null);
        Assert.Same(failure, await Record.ExceptionAsync(() =>
            fixture.InitializeOwnedClientAsync(client.Object, name, _ => Task.CompletedTask)));
        await fixture.DisposeAsync();
        client.Verify(value => value.DropDatabaseAsync(name, It.IsAny<CancellationToken>()), Times.Once);
        disposable.Verify(value => value.Dispose(), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedInitialization_DropsPrivateDatabaseAndDisposesOwnedClient(bool dropFails)
    {
        var name = "pw_mongodb_test_" + Guid.NewGuid().ToString("N");
        var initializationFailure = new IOException("private initialization failure");
        var cleanupFailure = new IOException("private cleanup failure");
        var database = new Mock<IMongoDatabase>(MockBehavior.Strict);
        database.SetupGet(value => value.DatabaseNamespace).Returns(new DatabaseNamespace(name));
        var client = new Mock<IMongoClient>(MockBehavior.Strict);
        client.Setup(value => value.GetDatabase(name, null)).Returns(database.Object);
        client.Setup(value => value.DropDatabaseAsync(name, It.IsAny<CancellationToken>()))
            .Returns(dropFails ? Task.FromException(cleanupFailure) : Task.CompletedTask);
        var disposable = client.As<IDisposable>();
        disposable.Setup(value => value.Dispose());
        var fixture = new MongoDbTestDatabaseFixture();
        Task Initialize() => fixture.InitializeOwnedClientAsync(client.Object, name, _ => Task.FromException(initializationFailure));
        if (dropFails)
        {
            var aggregate = await Assert.ThrowsAsync<AggregateException>(Initialize);
            Assert.Same(initializationFailure, aggregate.InnerExceptions[0]);
            Assert.Same(cleanupFailure, aggregate.InnerExceptions[1]);
        }
        else Assert.Same(initializationFailure, await Assert.ThrowsAsync<IOException>(Initialize));
        await fixture.DisposeAsync();
        disposable.Verify(value => value.Dispose(), Times.Once);
        client.Verify(value => value.DropDatabaseAsync(name, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrivateDatabaseDrop_IsAwaitedBeforeOwnedClientDisposal_EvenOnFailure(bool fail)
    {
        var drop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new IOException("private database drop failure");
        var name = "pw_mongodb_test_" + Guid.NewGuid().ToString("N");
        var client = new Mock<IMongoClient>(MockBehavior.Strict);
        client.Setup(value => value.DropDatabaseAsync(name, It.IsAny<CancellationToken>())).Returns(drop.Task);
        var disposable = client.As<IDisposable>();
        disposable.Setup(value => value.Dispose());
        var cleanup = MongoDbTestDatabaseFixture.DropOwnedDatabaseAsync(client.Object, name);
        disposable.Verify(value => value.Dispose(), Times.Never);
        Assert.False(cleanup.IsCompleted);
        if (fail) drop.SetException(failure);
        else drop.SetResult();
        if (fail) Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => cleanup));
        else await cleanup;
        disposable.Verify(value => value.Dispose(), Times.Once);
        client.VerifyAll();
    }
}
