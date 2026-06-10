using Phantom.Workspaces.Containers;

namespace Phantom.Workspaces.Data.MongoDB;

public sealed class MongoDbContainerDefinitionGenerator
{
    private const string MongoImageName = "mongo:latest";
    private const string MongoDataDirectory = "/data/db";
    private const int MongoContainerPort = 27017;

    public ContainerDefinition Generate(
        MongoDbContainerConnectionDefinition connectionDefinition)
    {
        ArgumentNullException.ThrowIfNull(connectionDefinition);

        return new ContainerDefinition
        {
            ContainerName = connectionDefinition.ContainerName,
            ImageName = MongoImageName,
            NetworkType = ContainerNetworkType.Bridge,
            EnvironmentVariables = new Dictionary<string, string>(),
            Mounts = new List<ContainerMountDefinition>
            {
                new ContainerMountDefinition
                {
                    Source = connectionDefinition.DataDirectory,
                    Target = MongoDataDirectory,
                    ReadOnly = false,
                },
            },
            PortMappings = new List<ContainerPortMappingDefinition>
            {
                new ContainerPortMappingDefinition
                {
                    SourcePort = connectionDefinition.HostPort ?? MongoContainerPort,
                    TargetPort = MongoContainerPort,
                },
            },
        };
    }
}
