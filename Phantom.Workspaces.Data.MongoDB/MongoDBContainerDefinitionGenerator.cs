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

    /// <summary>
    /// A fixed, non-ephemeral container hostname for the local Mongo (Atlas Local) container. The
    /// Atlas Local image derives its single-node replica-set member host from the container hostname;
    /// pinning it to a constant (rather than letting it default to the ephemeral container id) keeps
    /// the persisted <c>/data/db</c> replica-set config matching across container recreations and
    /// moving-<c>:latest</c> image refreshes, so a writable primary can always be elected (#1415).
    /// </summary>
    public const string ReplicaSetHostname = "phantom-mongo";

    private const string MongoDataDirectory = "/data/db";
    private const int MongoContainerPort = 27017;

    public ContainerDefinition Generate(
        MongoDbContainerConnectionDefinition connectionDefinition)
    {
        ArgumentNullException.ThrowIfNull(connectionDefinition);

        return new ContainerDefinition
        {
            ContainerName = connectionDefinition.ContainerName,
            Hostname = ReplicaSetHostname,
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
