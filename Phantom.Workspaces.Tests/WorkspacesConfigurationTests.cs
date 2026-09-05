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
