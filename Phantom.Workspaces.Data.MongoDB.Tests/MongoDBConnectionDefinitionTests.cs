using System.Text.Json;
using Json.Schema;
using Phantom.Workspaces.Data.MongoDB;

namespace Phantom.Workspaces.Data.MongoDB.Tests;

public sealed class MongoDbConnectionDefinitionTests
{
    [Fact]
    public void Value_RoundTrips_ForContainerBranch()
    {
        var definition = new MongoDbContainerConnectionDefinition
        {
            ContainerName = "mongo-db",
            DataDirectory = "C:\\mongo-data",
            DatabaseName = "workspace-db",
            CollectionName = "workspace-collection",
            HostPort = 37017,
        };

        var json = definition.ToJson();
        Assert.Contains("\"provider\":\"container\"", json);
        Assert.Contains("\"container-name\":\"mongo-db\"", json);
        Assert.Contains("\"data-directory\":\"C:\\\\mongo-data\"", json);
        Assert.Contains("\"database-name\":\"workspace-db\"", json);
        Assert.Contains("\"collection-name\":\"workspace-collection\"", json);
        Assert.Contains("\"host-port\":37017", json);
        var roundTrip = MongoDbConnectionDefinition.FromJson(json);

        var containerRoundTrip = Assert.IsType<MongoDbContainerConnectionDefinition>(roundTrip);

        Assert.Equal(MongoDbConnectionProvider.Container, containerRoundTrip.Provider);
        Assert.Equal("mongo-db", containerRoundTrip.ContainerName);
        Assert.Equal("C:\\mongo-data", containerRoundTrip.DataDirectory);
        Assert.Equal("workspace-db", containerRoundTrip.DatabaseName);
        Assert.Equal("workspace-collection", containerRoundTrip.CollectionName);
        Assert.Equal(37017, containerRoundTrip.HostPort);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var validation = MongoDbConnectionDefinitionJsonSchema.Value.Evaluate(
            root,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.Hierarchical,
                RequireFormatValidation = false,
            });

        Assert.Equal("container", root.GetProperty("provider").GetString());
        Assert.Equal("mongo-db", root.GetProperty("container-name").GetString());
        Assert.Equal("C:\\mongo-data", root.GetProperty("data-directory").GetString());
        Assert.Equal("workspace-db", root.GetProperty("database-name").GetString());
        Assert.Equal("workspace-collection", root.GetProperty("collection-name").GetString());
        Assert.Equal(37017, root.GetProperty("host-port").GetInt32());
        Assert.False(root.TryGetProperty("connection-string", out _));
        Assert.True(validation.IsValid, json);
    }

    [Fact]
    public void Value_RoundTrips_ForExternalBranch()
    {
        var definition = new MongoDbExternalConnectionDefinition
        {
            ConnectionString = "mongodb://localhost:27017",
            DatabaseName = "workspace-db",
            CollectionName = "workspace-collection",
        };

        var json = definition.ToJson();
        Assert.Contains("\"provider\":\"external\"", json);
        Assert.Contains("\"connection-string\":\"mongodb://localhost:27017\"", json);
        Assert.Contains("\"database-name\":\"workspace-db\"", json);
        Assert.Contains("\"collection-name\":\"workspace-collection\"", json);
        var roundTrip = MongoDbConnectionDefinition.FromJson(json);

        var externalRoundTrip = Assert.IsType<MongoDbExternalConnectionDefinition>(roundTrip);

        Assert.Equal(MongoDbConnectionProvider.External, externalRoundTrip.Provider);
        Assert.Equal("mongodb://localhost:27017", externalRoundTrip.ConnectionString);
        Assert.Equal("workspace-db", externalRoundTrip.DatabaseName);
        Assert.Equal("workspace-collection", externalRoundTrip.CollectionName);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var validation = MongoDbConnectionDefinitionJsonSchema.Value.Evaluate(
            root,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.Hierarchical,
                RequireFormatValidation = false,
            });

        Assert.Equal("external", root.GetProperty("provider").GetString());
        Assert.Equal("mongodb://localhost:27017", root.GetProperty("connection-string").GetString());
        Assert.Equal("workspace-db", root.GetProperty("database-name").GetString());
        Assert.Equal("workspace-collection", root.GetProperty("collection-name").GetString());
        Assert.False(root.TryGetProperty("container-name", out _));
        Assert.False(root.TryGetProperty("data-directory", out _));
        Assert.True(validation.IsValid, json);
    }
}
