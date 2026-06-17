using System.Text.Json;
using Phantom.Workspaces.Containers;
using Phantom.Workspaces.Data.MongoDB;
using Json.Schema;

namespace Phantom.Workspaces.Data.MongoDB.Tests;

public sealed class MongoDbContainerDefinitionGeneratorTests
{
    [Fact]
    public void Generate_WhenContainerConnectionDefinition_ReturnsMongoContainerDefinition()
    {
        var generator = new MongoDbContainerDefinitionGenerator();
        var connectionDefinition = new MongoDbContainerConnectionDefinition
        {
            ContainerName = "mongo-db",
            DataDirectory = "C:\\mongo-data",
            DatabaseName = "workspace-db",
            CollectionName = "workspace-collection",
            HostPort = 37017,
        };

        var containerDefinition = generator.Generate(connectionDefinition);

        Assert.Equal("mongo-db", containerDefinition.ContainerName);
        Assert.Equal("mongodb/mongodb-atlas-local:latest", containerDefinition.ImageName);
        Assert.Equal(ContainerNetworkType.Bridge, containerDefinition.NetworkType);
        Assert.Empty(containerDefinition.EnvironmentVariables);
        Assert.Equal(2, containerDefinition.Mounts.Count);
        Assert.Equal("C:\\mongo-data", containerDefinition.Mounts[0].Source);
        Assert.Equal("/data/db", containerDefinition.Mounts[0].Target);
        Assert.False(containerDefinition.Mounts[0].ReadOnly);
        Assert.Equal("C:\\mongo-data\\configdb", containerDefinition.Mounts[1].Source);
        Assert.Equal("/data/configdb", containerDefinition.Mounts[1].Target);
        Assert.False(containerDefinition.Mounts[1].ReadOnly);
        Assert.Single(containerDefinition.PortMappings);
        Assert.Equal(37017, containerDefinition.PortMappings[0].SourcePort);
        Assert.Equal(27017, containerDefinition.PortMappings[0].TargetPort);

        var json = containerDefinition.ToJson();
        var roundTrip = ContainerDefinition.FromJson(json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var validation = ContainerDefinitionJsonSchema.Value.Evaluate(
            root,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.Hierarchical,
                RequireFormatValidation = false,
            });

        Assert.Equal(containerDefinition.ContainerName, roundTrip.ContainerName);
        Assert.Equal(containerDefinition.ImageName, roundTrip.ImageName);
        Assert.True(validation.IsValid);
    }

    [Fact]
    public void Generate_WhenImageNameOverridden_UsesOverride()
    {
        var generator = new MongoDbContainerDefinitionGenerator();
        var connectionDefinition = new MongoDbContainerConnectionDefinition
        {
            ContainerName = "mongo-db",
            DataDirectory = "C:\\mongo-data",
            DatabaseName = "workspace-db",
            CollectionName = "workspace-collection",
            ImageName = "mongo:latest",
        };

        var containerDefinition = generator.Generate(connectionDefinition);

        Assert.Equal("mongo:latest", containerDefinition.ImageName);
    }

    [Fact]
    public void ContainerConnectionDefinition_WithImageName_RoundTripsThroughJson()
    {
        var definition = new MongoDbContainerConnectionDefinition
        {
            ContainerName = "mongo-db",
            DataDirectory = "C:\\mongo-data",
            DatabaseName = "workspace-db",
            CollectionName = "workspace-collection",
            ImageName = "mongodb/mongodb-atlas-local:8.0",
        };

        var roundTrip = Assert.IsType<MongoDbContainerConnectionDefinition>(
            MongoDbConnectionDefinition.FromJson(definition.ToJson()));

        Assert.Equal("mongodb/mongodb-atlas-local:8.0", roundTrip.ImageName);
    }
}
