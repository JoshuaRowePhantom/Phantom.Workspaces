using Phantom.Workspaces.Containers;

namespace Phantom.Workspaces.Data.MongoDB;

public sealed class MongoDbContainerDefinitionGenerator
{
    /// <summary>
    /// The default container image. Atlas Local bundles the <c>mongot</c> search process, so it
    /// supports Atlas Search and Vector Search (<c>$search</c> / <c>$vectorSearch</c>) locally -
    /// unlike the community <c>mongo</c> image. A connection definition may override this via
    /// <see cref="MongoDbContainerConnectionDefinition.ImageName"/>.
    /// </summary>
    public const string DefaultMongoImageName = "mongodb/mongodb-atlas-local:latest";

    private const string MongoDataDirectory = "/data/db";
    private const int MongoContainerPort = 27017;

    public ContainerDefinition Generate(
        MongoDbContainerConnectionDefinition connectionDefinition)
    {
        ArgumentNullException.ThrowIfNull(connectionDefinition);

        return new ContainerDefinition
        {
            ContainerName = connectionDefinition.ContainerName,
            ImageName = string.IsNullOrWhiteSpace(connectionDefinition.ImageName)
                ? DefaultMongoImageName
                : connectionDefinition.ImageName,
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
                new ContainerMountDefinition
                {
                    Source = Path.Combine(connectionDefinition.DataDirectory, "configdb"),
                    Target = "/data/configdb",
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
