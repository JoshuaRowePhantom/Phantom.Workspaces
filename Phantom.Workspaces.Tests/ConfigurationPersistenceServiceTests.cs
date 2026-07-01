using System.IO;
using System.Threading.Tasks;
using Phantom.Workspaces.Configuration;

namespace Phantom.Workspaces.Tests;

public sealed class ConfigurationPersistenceServiceTests
{
    [PhantomAvaloniaFact]
    public async Task LoadAsync_WhenFileMissing_ReturnsDefaults()
    {
        var path = CreateTempConfigPath();
        var service = new ConfigurationPersistenceService(path);

        var configuration = await service.LoadAsync();

        Assert.Equal(1, configuration.Version);
        Assert.Equal(DataAccessMode.LocalMongoContainer, configuration.DataAccess.Mode);
        Assert.False(configuration.RemoteHosting.Enabled);
        Assert.Equal(DevTunnelAccessMode.Private, configuration.DevTunnel.AccessMode);
    }

    [PhantomAvaloniaFact]
    public async Task SaveThenLoad_RoundTripsConfiguration()
    {
        var path = CreateTempConfigPath();
        var service = new ConfigurationPersistenceService(path);
        var configuration = new WorkspacesConfiguration
        {
            DataAccess = new DataAccessConnectionProfile
            {
                Mode = DataAccessMode.DevTunnelWeb,
                WebEndpoint = "https://example.devtunnels.ms/",
            },
            RemoteHosting = new RemoteHostingSettings
            {
                Enabled = true,
                ListenUrl = "http://localhost:6001",
            },
            DevTunnel = new DevTunnelConfiguration
            {
                TunnelName = "workspaces-host",
                HostedPorts = [5280],
                AccessMode = DevTunnelAccessMode.Private,
            },
        };

        try
        {
            await service.SaveAsync(configuration);
            var reloaded = await service.LoadAsync();

            Assert.Equal(
                ConfigurationPersistenceService.Serialize(configuration),
                ConfigurationPersistenceService.Serialize(reloaded));
        }
        finally
        {
            DeleteTempConfig(path);
        }
    }

    [PhantomAvaloniaFact]
    public async Task SaveAsync_DoesNotPersistRawSecrets()
    {
        var path = CreateTempConfigPath();
        var service = new ConfigurationPersistenceService(path);
        var configuration = new WorkspacesConfiguration
        {
            DataAccess = new DataAccessConnectionProfile
            {
                Mode = DataAccessMode.DevTunnelWeb,
                WebEndpoint = "https://example.devtunnels.ms/",
            },
            DevTunnel = new DevTunnelConfiguration
            {
                AccessMode = DevTunnelAccessMode.Private,
            },
        };

        try
        {
            await service.SaveAsync(configuration);
            var json = await File.ReadAllTextAsync(path);

            // No raw-token-bearing properties exist in the serialized document.
            Assert.DoesNotContain("\"accessToken\":", json, System.StringComparison.Ordinal);
            Assert.DoesNotContain("\"token\":", json, System.StringComparison.Ordinal);
            Assert.DoesNotContain("\"connectionString\":", json, System.StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempConfig(path);
        }
    }

    [PhantomAvaloniaFact]
    public void ToRepositorySource_LocalMongoContainer_MapsContainerFields()
    {
        var configuration = new WorkspacesConfiguration
        {
            DataAccess = new DataAccessConnectionProfile
            {
                Mode = DataAccessMode.LocalMongoContainer,
                MongoContainerName = "mongodb",
                MongoRootCollectionName = "entities",
                MongoDatabaseName = "phantom-workspaces",
                MongoHostPort = 27017,
            },
        };

        var repositorySource = configuration.ToRepositorySource();

        var mongo = Assert.IsType<MongoDbRepositorySource>(repositorySource);
        Assert.Equal("mongodb", mongo.ContainerName);
        Assert.Equal("entities", mongo.RootCollectionName);
        Assert.Equal(27017, mongo.HostPort);
    }

    [PhantomAvaloniaFact]
    public void ToRepositorySource_Web_MapsEndpoint()
    {
        var configuration = new WorkspacesConfiguration
        {
            DataAccess = new DataAccessConnectionProfile
            {
                Mode = DataAccessMode.Web,
                WebEndpoint = "https://workspaces.example/",
            },
        };

        var repositorySource = configuration.ToRepositorySource();

        var web = Assert.IsType<WebRepositorySource>(repositorySource);
        Assert.Equal("https://workspaces.example/", web.Endpoint);
    }

    [PhantomAvaloniaFact]
    public void ToRepositorySource_Web_DoesNotUseGitHubAuthToken()
    {
        var configuration = new WorkspacesConfiguration
        {
            DataAccess = new DataAccessConnectionProfile
            {
                Mode = DataAccessMode.Web,
                WebEndpoint = "https://workspaces.example/",
            },
        };

        var web = Assert.IsType<WebRepositorySource>(configuration.ToRepositorySource());
        Assert.False(web.UseGitHubAuthToken);
    }

    [PhantomAvaloniaFact]
    public void ToRepositorySource_DevTunnelWeb_UsesGitHubAuthToken()
    {
        var configuration = new WorkspacesConfiguration
        {
            DataAccess = new DataAccessConnectionProfile
            {
                Mode = DataAccessMode.DevTunnelWeb,
                WebEndpoint = "https://host.devtunnels.ms/",
            },
        };

        var web = Assert.IsType<WebRepositorySource>(configuration.ToRepositorySource());
        Assert.Equal("https://host.devtunnels.ms/", web.Endpoint);
        Assert.True(web.UseGitHubAuthToken);
    }

    [PhantomAvaloniaFact]
    public void ToRepositorySource_RemoteMongo_Throws()
    {
        var configuration = new WorkspacesConfiguration
        {
            DataAccess = new DataAccessConnectionProfile
            {
                Mode = DataAccessMode.RemoteMongo,
                MongoConnectionStringSource = "MONGO_CONNECTION",
            },
        };

        Assert.Throws<System.InvalidOperationException>(() => configuration.ToRepositorySource());
    }

    [PhantomAvaloniaFact]
    public void ToRepositorySource_WebWithoutEndpoint_Throws()
    {
        var configuration = new WorkspacesConfiguration
        {
            DataAccess = new DataAccessConnectionProfile { Mode = DataAccessMode.Web },
        };

        Assert.Throws<System.InvalidOperationException>(() => configuration.ToRepositorySource());
    }

    private static string CreateTempConfigPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"phantom-config-{System.Guid.NewGuid():N}");
        return Path.Combine(directory, "config.json");
    }

    private static void DeleteTempConfig(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
