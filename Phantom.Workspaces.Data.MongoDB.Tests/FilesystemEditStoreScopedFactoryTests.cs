using MongoDB.Driver;
using Moq;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Data.MongoDB.Tests;

public sealed class FilesystemEditStoreScopedFactoryTests
{
    [Theory]
    [InlineData("container")]
    [InlineData("external")]
    public async Task ScopedClient_ResolvesConfigurationWithoutDefaultBroker_AndIsNotDisposed(string provider)
    {
        using var cancellation = new CancellationTokenSource();
        var client = new Mock<IMongoClient>(MockBehavior.Strict);
        var database = new Mock<IMongoDatabase>(MockBehavior.Strict);
        var collection = new Mock<IMongoCollection<MongoDbFilesystemEditDocument>>();
        client.Setup(value => value.GetDatabase("private", null)).Returns(database.Object);
        database.Setup(value => value.GetCollection<MongoDbFilesystemEditDocument>("edits", null)).Returns(collection.Object);
        var disposable = client.As<IDisposable>();
        var definition = ChatHistoryProviderDefinition.CreateMongoDb(provider, "private", "edits",
            containerName: "never-start", dataDirectory: "never-create", hostPort: 12345,
            connectionString: "mongodb://127.0.0.1:37027/?directConnection=true");
        var calls = 0;
        var store = await FilesystemEditStoreFactory.CreateAsync(definition.ToJson(), (actual, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            if (provider == "container")
            {
                var container = Assert.IsType<MongoDbContainerConnectionDefinition>(actual);
                Assert.Equal("never-start", container.ContainerName);
                Assert.Equal("private", container.DatabaseName);
            }
            else Assert.Equal("private", Assert.IsType<MongoDbExternalConnectionDefinition>(actual).DatabaseName);
            calls++;
            return ValueTask.FromResult(client.Object);
        }, cancellation.Token);
        Assert.IsType<MongoDbFilesystemEditStore>(store);
        Assert.Equal(1, calls);
        disposable.Verify(value => value.Dispose(), Times.Never);
        client.VerifyAll();
        database.VerifyAll();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedScopedClient_DoesNotFallBackOrReplay(bool invalidResult)
    {
        var definition = ChatHistoryProviderDefinition.CreateMongoDb("external", "private", "edits",
            connectionString: "mongodb://127.0.0.1:37027/?directConnection=true");
        var original = new IOException("scoped client failure");
        var calls = 0;
        var failure = await Record.ExceptionAsync(async () => await FilesystemEditStoreFactory.CreateAsync(
            definition.ToJson(), (_, _) =>
            {
                calls++;
                return invalidResult ? ValueTask.FromResult<IMongoClient>(null!) : ValueTask.FromException<IMongoClient>(original);
            }, CancellationToken.None));
        if (invalidResult) Assert.Contains("factory returned no client", Assert.IsType<InvalidOperationException>(failure).Message);
        else Assert.Same(original, failure);
        Assert.Equal(1, calls);
    }
}
