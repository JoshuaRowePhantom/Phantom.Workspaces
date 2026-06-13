using System.IO;
using System.Threading.Tasks;
using Phantom.Workspaces.Configuration;

namespace Phantom.Workspaces.Tests;

public sealed class ConfigurationPersistenceServiceTests
{
    [AvaloniaFact]
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

    [AvaloniaFact]
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
                DevTunnelTokenSource = "DEVTUNNEL_TOKEN",
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
                AccessMode = DevTunnelAccessMode.Token,
                AccessTokenSource = "DEVTUNNEL_TOKEN",
            },
            Visual = new VisualSettings { Theme = "FluentDark" },
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

    [AvaloniaFact]
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
                DevTunnelTokenSource = "DEVTUNNEL_TOKEN",
            },
            DevTunnel = new DevTunnelConfiguration
            {
                AccessMode = DevTunnelAccessMode.Token,
                AccessTokenSource = "DEVTUNNEL_TOKEN",
            },
        };

        try
        {
            await service.SaveAsync(configuration);
            var json = await File.ReadAllTextAsync(path);

            // Only secret sources (env var names) are stored.
            Assert.Contains("devTunnelTokenSource", json, System.StringComparison.Ordinal);
            Assert.Contains("accessTokenSource", json, System.StringComparison.Ordinal);

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

    [AvaloniaFact]
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

        Assert.Equal(RepositorySourceType.MongoDb, repositorySource.SourceType);
        Assert.Equal("mongodb", repositorySource.MongoDbContainerName);
        Assert.Equal("entities", repositorySource.MongoDbRootCollectionName);
        Assert.Equal(27017, repositorySource.MongoDbHostPort);
    }

    [AvaloniaFact]
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

        Assert.Equal(RepositorySourceType.Web, repositorySource.SourceType);
        Assert.Equal("https://workspaces.example/", repositorySource.RawValue);
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
