using System.Text.Json;
using Json.Schema;
using Phantom.Workspaces.Data.MongoDB;

namespace Phantom.Workspaces.Data.MongoDB.Tests;

public sealed class MongoDBConnectionDefinitionTests
{
    [Fact]
    public void Value_RoundTrips_ForContainerBranch()
    {
        var definition = new MongoDBContainerConnectionDefinition
        {
            ContainerName = "mongo-db",
            DataDirectory = "C:\\mongo-data",
        };

        var json = definition.ToJson();
        Assert.Contains("\"provider\":\"container\"", json);
        Assert.Contains("\"container-name\":\"mongo-db\"", json);
        Assert.Contains("\"data-directory\":\"C:\\\\mongo-data\"", json);
        var roundTrip = MongoDBConnectionDefinition.FromJson(json);

        var containerRoundTrip = Assert.IsType<MongoDBContainerConnectionDefinition>(roundTrip);

        Assert.Equal(MongoDBConnectionProvider.Container, containerRoundTrip.Provider);
        Assert.Equal("mongo-db", containerRoundTrip.ContainerName);
        Assert.Equal("C:\\mongo-data", containerRoundTrip.DataDirectory);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var validation = MongoDBConnectionDefinitionJsonSchema.Value.Evaluate(
            root,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.Hierarchical,
                RequireFormatValidation = false,
            });

        Assert.Equal("container", root.GetProperty("provider").GetString());
        Assert.Equal("mongo-db", root.GetProperty("container-name").GetString());
        Assert.Equal("C:\\mongo-data", root.GetProperty("data-directory").GetString());
        Assert.False(root.TryGetProperty("connection-string", out _));
        Assert.True(validation.IsValid, json);
    }

    [Fact]
    public void Value_RoundTrips_ForExternalBranch()
    {
        var definition = new MongoDBExternalConnectionDefinition
        {
            ConnectionString = "mongodb://localhost:27017",
        };

        var json = definition.ToJson();
        Assert.Contains("\"provider\":\"external\"", json);
        Assert.Contains("\"connection-string\":\"mongodb://localhost:27017\"", json);
        var roundTrip = MongoDBConnectionDefinition.FromJson(json);

        var externalRoundTrip = Assert.IsType<MongoDBExternalConnectionDefinition>(roundTrip);

        Assert.Equal(MongoDBConnectionProvider.External, externalRoundTrip.Provider);
        Assert.Equal("mongodb://localhost:27017", externalRoundTrip.ConnectionString);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var validation = MongoDBConnectionDefinitionJsonSchema.Value.Evaluate(
            root,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.Hierarchical,
                RequireFormatValidation = false,
            });

        Assert.Equal("external", root.GetProperty("provider").GetString());
        Assert.Equal("mongodb://localhost:27017", root.GetProperty("connection-string").GetString());
        Assert.False(root.TryGetProperty("container-name", out _));
        Assert.False(root.TryGetProperty("data-directory", out _));
        Assert.True(validation.IsValid, json);
    }
}
