using System.Text.Json;
using Json.Schema;
using Phantom.Workspaces.Containers;

namespace Phantom.Workspaces.Containers.Tests;

public sealed class ContainerDefinitionTests
{
    [Theory]
    [InlineData(ContainerNetworkType.Bridge, "bridge")]
    [InlineData(ContainerNetworkType.Host, "host")]
    [InlineData(ContainerNetworkType.None, "none")]
    [InlineData(ContainerNetworkType.Container, "container")]
    public void Value_RoundTrips_ForEachNetworkType(
        ContainerNetworkType networkType,
        string expectedNetworkType)
    {
        var definition = new ContainerDefinition
        {
            ContainerName = "mongo-db",
            ImageName = "mongo:latest",
            NetworkType = networkType,
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["MONGO_INITDB_ROOT_USERNAME"] = "root",
            },
            Mounts = new List<ContainerMountDefinition>
            {
                new()
                {
                    Source = "C:\\mongo-data",
                    Target = "/data/db",
                    ReadOnly = false,
                },
                new()
                {
                    Source = "C:\\mongo-config",
                    Target = "/config",
                    ReadOnly = true,
                },
            },
        };

        var json = definition.ToJson();
        var roundTrip = ContainerDefinition.FromJson(json);

        Assert.Equal(definition.ContainerName, roundTrip.ContainerName);
        Assert.Equal(definition.ImageName, roundTrip.ImageName);
        Assert.Equal(definition.NetworkType, roundTrip.NetworkType);
        Assert.Single(roundTrip.EnvironmentVariables);
        Assert.Equal("root", roundTrip.EnvironmentVariables["MONGO_INITDB_ROOT_USERNAME"]);
        Assert.Equal(definition.Mounts.Count, roundTrip.Mounts.Count);
        Assert.Equal("C:\\mongo-data", roundTrip.Mounts[0].Source);
        Assert.Equal("/data/db", roundTrip.Mounts[0].Target);
        Assert.Equal(definition.Mounts[0].ReadOnly, roundTrip.Mounts[0].ReadOnly);
        Assert.Equal("C:\\mongo-config", roundTrip.Mounts[1].Source);
        Assert.Equal("/config", roundTrip.Mounts[1].Target);
        Assert.Equal(definition.Mounts[1].ReadOnly, roundTrip.Mounts[1].ReadOnly);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var validation = ContainerDefinitionJsonSchema.Value.Evaluate(
            root,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.Hierarchical,
                RequireFormatValidation = false,
            });

        Assert.Equal("mongo-db", root.GetProperty("container-name").GetString());
        Assert.Equal("mongo:latest", root.GetProperty("image-name").GetString());
        Assert.Equal(expectedNetworkType, root.GetProperty("network-type").GetString());
        Assert.Equal("root", root.GetProperty("environment-variables").GetProperty("MONGO_INITDB_ROOT_USERNAME").GetString());
        Assert.Equal(2, root.GetProperty("mounts").GetArrayLength());
        Assert.True(validation.IsValid);
    }
}
