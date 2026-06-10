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
        Assert.Equal("mongo:latest", containerDefinition.ImageName);
        Assert.Equal(ContainerNetworkType.Bridge, containerDefinition.NetworkType);
        Assert.Empty(containerDefinition.EnvironmentVariables);
        Assert.Single(containerDefinition.Mounts);
        Assert.Equal("C:\\mongo-data", containerDefinition.Mounts[0].Source);
        Assert.Equal("/data/db", containerDefinition.Mounts[0].Target);
        Assert.False(containerDefinition.Mounts[0].ReadOnly);
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
}
