using MongoDB.Bson;
using MongoDB.Driver;
using Moq;

namespace Phantom.Workspaces.Data.MongoDB.Tests;

public sealed class MongoDbFixtureConnectionTests
{
    [Fact]
    public void UninitializedFixture_CannotExposeDefaultConnection_EvenWithExternalOptIn()
    {
        var fixture = new MongoDbTestDatabaseFixture("mongodb://127.0.0.1:37027/?directConnection=true");
        var failure = Assert.Throws<InvalidOperationException>(() => fixture.ConnectionDefinition);
        Assert.Contains("must be initialized", failure.Message);
    }

    [Fact]
    public async Task DefaultMode_PreservesContainerConfiguration_AndDoesNotDisposeBrokerClient()
    {
        var database = new Mock<IMongoDatabase>(MockBehavior.Loose);
        var client = new Mock<IMongoClient>(MockBehavior.Strict);
        client.Setup(value => value.GetDatabase(MongoDbTestDatabaseFixture.DatabaseName, null)).Returns(database.Object);
        var disposable = client.As<IDisposable>();
        var connections = 0;
        var fixture = new MongoDbTestDatabaseFixture(null, (definition, _) =>
        {
            var container = Assert.IsType<MongoDbContainerConnectionDefinition>(definition);
            Assert.Equal(MongoDbTestDatabaseFixture.ContainerName, container.ContainerName);
            Assert.Equal(MongoDbTestDatabaseFixture.HostPort, container.HostPort);
            Assert.Equal(MongoDbTestDatabaseFixture.DatabaseName, container.DatabaseName);
            connections++;
            return ValueTask.FromResult(client.Object);
        });
        await fixture.InitializeAsync();
        Assert.IsType<MongoDbContainerConnectionDefinition>(fixture.ConnectionDefinition);
        await fixture.DisposeAsync();
        Assert.Throws<InvalidOperationException>(() => fixture.ConnectionDefinition);
        Assert.Equal(1, connections);
        disposable.Verify(value => value.Dispose(), Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not-a-mongodb-uri")]
    public async Task InvalidExplicitOverride_DoesNotFallBackToContainer(string connection)
    {
        var connections = 0;
        var fixture = new MongoDbTestDatabaseFixture(connection, (_, _) =>
        {
            connections++;
            throw new InvalidOperationException("Default connector must never be reached.");
        });
        Assert.NotNull(await Record.ExceptionAsync(fixture.InitializeAsync));
        await fixture.DisposeAsync();
        Assert.Equal(0, connections);
    }

    [Fact]
    public async Task ExternalMode_UsesOwnedClientAndGuidDatabase_NeverDefaultConnector()
    {
        const string connection = "mongodb://127.0.0.1:37027/?directConnection=true";
        var client = new Mock<IMongoClient>(MockBehavior.Strict);
        var database = new Mock<IMongoDatabase>(MockBehavior.Loose);
        string? name = null;
        client.Setup(value => value.GetDatabase(It.IsAny<string>(), null))
            .Callback<string, MongoDatabaseSettings?>((actual, _) => name = actual)
            .Returns(database.Object);
        database.SetupGet(value => value.DatabaseNamespace).Returns(() => new DatabaseNamespace(name!));
        database.Setup(value => value.RunCommandAsync(
                It.IsAny<Command<BsonDocument>>(), It.IsAny<ReadPreference>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BsonDocument("ok", 1));
        client.Setup(value => value.DropDatabaseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((actual, _) => Assert.Equal(name, actual)).Returns(Task.CompletedTask);
        var disposable = client.As<IDisposable>();
        disposable.Setup(value => value.Dispose());
        var fixture = new MongoDbTestDatabaseFixture(connection,
            (_, _) => throw new InvalidOperationException("Container/default connector must never run."),
            actual =>
            {
                Assert.Equal(connection, actual);
                return client.Object;
            });
        await fixture.InitializeAsync();
        var definition = Assert.IsType<MongoDbExternalConnectionDefinition>(fixture.ConnectionDefinition);
        Assert.Equal(connection, definition.ConnectionString);
        Assert.Equal(name, definition.DatabaseName);
        Assert.Equal(name, fixture.Database.DatabaseNamespace.DatabaseName);
        Assert.Matches("^pw_mongodb_test_[0-9a-f]{32}$", name!);
        await fixture.DisposeAsync();
        await fixture.DisposeAsync();
        disposable.Verify(value => value.Dispose(), Times.Once);
        client.VerifyAll();
    }
}
