using MongoDB.Driver;
using Moq;
using Phantom.Workspaces.Data.MongoDB;

namespace Phantom.Workspaces.Tests;

public sealed class EntityRepositoryMongoDbInitializationTests
{
    [Fact]
    public void SharedMongoFixture_UsesThisProjectsActualAsyncLifetime()
    {
        Assert.IsAssignableFrom<IAsyncLifetime>(new EntityRepositoryMongoDbFixture());
        Assert.IsAssignableFrom<ICollectionFixture<EntityRepositoryMongoDbFixture>>(
            new MongoDbTestDatabaseCollectionFixture());
    }

    [Theory]
    [InlineData(null, "phantom-workspaces")]
    [InlineData("private-database", "private-database")]
    public async Task ScopedClientFactory_BypassesDefaultConnection_AndReceivesSourceAndCancellation(
        string? databaseName, string expectedDatabase)
    {
        var original = new IOException("injected client connection failure");
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var options = new EntityRepositoryInitializationOptions
        {
            RepositorySource = new MongoDbRepositorySource("never-start-this-container", "entities",
                "never-create-this-directory", databaseName, 12345),
            MongoDbClientFactory = (definition, token) =>
            {
                var container = Assert.IsType<MongoDbContainerConnectionDefinition>(definition);
                Assert.Equal("never-start-this-container", container.ContainerName);
                Assert.Equal("never-create-this-directory", container.DataDirectory);
                Assert.Equal(expectedDatabase, container.DatabaseName);
                Assert.Equal("entities", container.CollectionName);
                Assert.Equal(12345, container.HostPort);
                Assert.Equal(cancellation.Token, token);
                calls++;
                return ValueTask.FromException<IMongoClient>(original);
            },
            MongoDbEntityDataAccessLayerFactory = (_, _) => throw new InvalidOperationException("DAL must not start."),
        };
        Assert.Same(original, await Record.ExceptionAsync(() => EntityRepository.CreateAsync(options, cancellation.Token)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ScopedFactories_AreIndependentDuringConcurrentInitialization_AndDoNotLeak()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFailure = new IOException("first scoped connection");
        var secondFailure = new IOException("second scoped connection");
        var source = new MongoDbRepositorySource("never-start", "entities", "never-create", "private");
        var first = EntityRepository.CreateAsync(new EntityRepositoryInitializationOptions
        {
            RepositorySource = source,
            MongoDbClientFactory = async (_, _) =>
            {
                entered.SetResult();
                await release.Task;
                throw firstFailure;
            },
        }, TestContext.Current.CancellationToken);
        await entered.Task;
        try
        {
            Assert.Same(secondFailure, await Record.ExceptionAsync(() => EntityRepository.CreateAsync(new EntityRepositoryInitializationOptions
            {
                RepositorySource = source,
                MongoDbClientFactory = (_, _) => ValueTask.FromException<IMongoClient>(secondFailure),
            }, TestContext.Current.CancellationToken)));
        }
        finally { release.SetResult(); }
        Assert.Same(firstFailure, await Record.ExceptionAsync(() => first));
        var invalid = await Assert.ThrowsAsync<InvalidOperationException>(() => EntityRepository.CreateAsync(
            new MongoDbRepositorySource("", "entities")));
        Assert.Contains("container name is required", invalid.Message);
    }

    [Fact]
    public async Task InvalidFactoryResult_FailsWithoutFallback()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => EntityRepository.CreateAsync(new EntityRepositoryInitializationOptions
        {
            RepositorySource = new MongoDbRepositorySource("never-start", "entities"),
            MongoDbClientFactory = (_, _) => ValueTask.FromResult<IMongoClient>(null!),
        }, TestContext.Current.CancellationToken));
        Assert.Contains("client factory returned no client", exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedDalFactory_DoesNotDisposeBorrowedFixtureClient(bool invalidResult)
    {
        var client = new Mock<IMongoClient>(MockBehavior.Strict);
        var database = new Mock<IMongoDatabase>(MockBehavior.Strict);
        client.Setup(value => value.GetDatabase("private", null)).Returns(database.Object);
        var disposable = client.As<IDisposable>();
        var failure = new IOException("DAL initialization failed");
        var actualFailure = await Record.ExceptionAsync(() => EntityRepository.CreateAsync(new EntityRepositoryInitializationOptions
        {
            RepositorySource = new MongoDbRepositorySource("never-start", "entities", DatabaseName: "private"),
            MongoDbClientFactory = (_, _) => ValueTask.FromResult(client.Object),
            MongoDbEntityDataAccessLayerFactory = (actual, collection) =>
            {
                Assert.Same(database.Object, actual);
                Assert.Equal("entities", collection);
                if (!invalidResult) throw failure;
                return null!;
            },
        }, TestContext.Current.CancellationToken));
        if (invalidResult) Assert.Contains("factory returned no layer", Assert.IsType<InvalidOperationException>(actualFailure).Message);
        else Assert.Same(failure, actualFailure);
        disposable.Verify(value => value.Dispose(), Times.Never);
        client.VerifyAll();
    }

    [Theory]
    [InlineData("", "entities", "container name")]
    [InlineData("container", "", "root collection")]
    public async Task DefaultConnection_StillValidatesSourceBeforeConnecting(string container, string collection, string message)
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EntityRepository.CreateAsync(new MongoDbRepositorySource(container, collection)));
        Assert.Contains(message, failure.Message);
    }
}
