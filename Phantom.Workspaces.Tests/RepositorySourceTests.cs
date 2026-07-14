using System.IO;
using System.Threading.Tasks;
using Phantom.Workspaces.Configuration;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Tests;

public sealed class RepositorySourceTests
{
    [PhantomAvaloniaFact]
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

    [PhantomAvaloniaFact]
    public void DevTunnelWeb_WithExplicitEndpoint_ProjectsToWebSourceUsingGitHubToken()
    {
        var configuration = new WorkspacesConfiguration
        {
            DataAccess = new DataAccessConnectionProfile
            {
                Mode = DataAccessMode.DevTunnelWeb,
                WebEndpoint = "https://example-5280.usw2.devtunnels.ms/",
            },
        };

        var web = Assert.IsType<WebRepositorySource>(configuration.ToRepositorySource());
        Assert.Equal("https://example-5280.usw2.devtunnels.ms/", web.Endpoint);
        Assert.True(web.UseGitHubAuthToken);
    }

    [PhantomAvaloniaFact]
    public void DevTunnelWeb_WithTunnelNameAndNoEndpoint_ProjectsToDevTunnelNameSource()
    {
        var configuration = new WorkspacesConfiguration
        {
            DataAccess = new DataAccessConnectionProfile { Mode = DataAccessMode.DevTunnelWeb },
            DevTunnel = new DevTunnelConfiguration
            {
                TunnelName = "phantom-workspaces-playspace",
                AccessMode = DevTunnelAccessMode.Private,
            },
        };

        var source = Assert.IsType<DevTunnelNameRepositorySource>(configuration.ToRepositorySource());
        Assert.Equal("phantom-workspaces-playspace", source.TunnelName);
        Assert.Equal(DevTunnelAccessMode.Private, source.AccessMode);
    }

    [PhantomAvaloniaFact]
    public void DevTunnelWeb_WithNeitherEndpointNorTunnelName_Throws()
    {
        var configuration = new WorkspacesConfiguration
        {
            DataAccess = new DataAccessConnectionProfile { Mode = DataAccessMode.DevTunnelWeb },
        };

        Assert.Throws<System.InvalidOperationException>(() => configuration.ToRepositorySource());
    }
}
