using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Phantom.Workspaces.Configuration;

namespace Phantom.Workspaces.Tests;

public sealed class WorkspacesConfigurationTests
{
    [AvaloniaFact]
    public void RemoteHostingSettings_LegacySingleListenUrl_MigratesToList()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        var settings = JsonSerializer.Deserialize<RemoteHostingSettings>(
            """{"listenUrl":"http://0.0.0.0:5280"}""",
            options);

        Assert.NotNull(settings);
        Assert.Equal("http://0.0.0.0:5280", Assert.Single(settings!.ListenUrls));
        Assert.Equal("http://0.0.0.0:5280", settings.PrimaryListenUrl);
    }

    [AvaloniaFact]
    public async Task WorkspacesConfiguration_RemoteHostingListenUrls_RoundTripsThroughPersistence()
    {
        var path = CreateTempConfigPath();
        var service = new ConfigurationPersistenceService(path);
        var configuration = new WorkspacesConfiguration
        {
            RemoteHosting = new RemoteHostingSettings
            {
                Enabled = true,
                ListenUrls = ["http://127.0.0.1:5280", "http://127.0.0.1:5281"],
            },
        };

        try
        {
            await service.SaveAsync(configuration);
            var reloaded = await service.LoadAsync();

            Assert.Equal(
                ["http://127.0.0.1:5280", "http://127.0.0.1:5281"],
                reloaded.RemoteHosting.ListenUrls);
        }
        finally
        {
            DeleteTempConfig(path);
        }
    }

    [AvaloniaFact]
    public void Configuration_LegacyUseGitHubAuthToken_MigratesToSchemeGithub()
    {
        // A dev-tunnel config with no explicit Authentication must migrate to the github scheme,
        // preserving the retired useGitHubAuthToken default.
        var configuration = new WorkspacesConfiguration
        {
            DataAccess = new DataAccessConnectionProfile
            {
                Mode = DataAccessMode.DevTunnelWeb,
                WebEndpoint = "https://example.devtunnels.ms/",
            },
        };

        var web = Assert.IsType<global::Phantom.Workspaces.WebRepositorySource>(configuration.ToRepositorySource());

        Assert.True(web.UseGitHubAuthToken);
        Assert.NotNull(web.Authentication);
        Assert.Equal(RemoteAuthentication.GithubScheme, web.Authentication!.Scheme);
    }

    [AvaloniaFact]
    public void Configuration_DevTunnelAnonymousAccessMode_MapsToSchemeAnonymous()
    {
        // Anonymous access mode must derive the anonymous scheme (and no GitHub token) when no
        // explicit Authentication is configured.
        var configuration = new WorkspacesConfiguration
        {
            DataAccess = new DataAccessConnectionProfile { Mode = DataAccessMode.DevTunnelWeb },
            DevTunnel = new DevTunnelConfiguration
            {
                TunnelName = "phantom-tunnel",
                AccessMode = DevTunnelAccessMode.Anonymous,
            },
        };

        var source = Assert.IsType<global::Phantom.Workspaces.DevTunnelNameRepositorySource>(
            configuration.ToRepositorySource());

        Assert.NotNull(source.Authentication);
        Assert.Equal(RemoteAuthentication.AnonymousScheme, source.Authentication!.Scheme);
    }

    [AvaloniaFact]
    public void Configuration_ExplicitAuthentication_OverridesDerivedDefault()
    {
        // An explicit Authentication on the data-access profile must win over the access-mode default.
        var configuration = new WorkspacesConfiguration
        {
            DataAccess = new DataAccessConnectionProfile
            {
                Mode = DataAccessMode.DevTunnelWeb,
                Authentication = new RemoteAuthentication(RemoteAuthentication.EntraScheme),
            },
            DevTunnel = new DevTunnelConfiguration
            {
                TunnelName = "phantom-tunnel",
                AccessMode = DevTunnelAccessMode.Anonymous,
            },
        };

        var source = Assert.IsType<global::Phantom.Workspaces.DevTunnelNameRepositorySource>(
            configuration.ToRepositorySource());

        Assert.Equal(RemoteAuthentication.EntraScheme, source.Authentication!.Scheme);
    }

    private static string CreateTempConfigPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"phantom-config-{Guid.NewGuid():N}");
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
