using Phantom.Workspaces.Containers;

namespace Phantom.Workspaces.Data.MongoDB;

public sealed class MongoDBContainerDefinitionGenerator
{
    private const string MongoImageName = "mongo:latest";
    private const string MongoDataDirectory = "/data/db";

    public ContainerDefinition Generate(
        MongoDBContainerConnectionDefinition connectionDefinition)
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
        };
    }
}
