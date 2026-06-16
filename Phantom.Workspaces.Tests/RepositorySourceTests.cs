using System.IO;
using System.Threading.Tasks;
using Phantom.Workspaces.Configuration;

namespace Phantom.Workspaces.Tests;

public sealed class RepositorySourceTests
{
    [AvaloniaFact]
    public async Task ConfigurationFile_ProjectsToRepositorySource()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"phantom-config-{System.Guid.NewGuid():N}");
        var path = Path.Combine(directory, "config.json");
        try
        {
            var service = new ConfigurationPersistenceService(path);
            await service.SaveAsync(new WorkspacesConfiguration
            {
                DataAccess = new DataAccessConnectionProfile
                {
                    Mode = DataAccessMode.LocalMongoContainer,
                    MongoContainerName = "phantom-mongodb",
                    MongoRootCollectionName = "entities",
                    MongoDataDirectory = "C:/mongo-data",
                },
            });

            // The CLI accepts only a config file path; the startup flow loads it and projects it.
            Assert.True(CommandLineOptions.TryGetConfigurationFilePath([path], out var resolvedPath));
            var configuration = await new ConfigurationPersistenceService(resolvedPath!).LoadAsync();
            var mongo = Assert.IsType<MongoDbRepositorySource>(configuration.ToRepositorySource());
            Assert.Equal("phantom-mongodb", mongo.ContainerName);
            Assert.Equal("entities", mongo.RootCollectionName);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
