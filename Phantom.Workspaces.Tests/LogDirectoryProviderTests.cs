using System.IO;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Services.Logging;

namespace Phantom.Workspaces.Tests;

public sealed class LogDirectoryProviderTests
{
    [Fact]
    public void LogDirectoryProvider_ResolvesLogDirectory_FromWorkspacesConfiguration()
    {
        var directory = CreateTempDirectoryPath();
        try
        {
            var configuration = new WorkspacesConfiguration { LogDirectory = directory };
            var provider = new LogDirectoryProvider(configuration, configurationPath: null);

            Assert.Equal(directory, provider.LogDirectory);
            Assert.True(Directory.Exists(directory));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void LogDirectoryProvider_WhenConfigurationUnset_UsesConfigurationServiceDefault()
    {
        var configDirectory = CreateTempDirectoryPath();
        var configPath = Path.Combine(configDirectory, "config.json");
        try
        {
            var configuration = new WorkspacesConfiguration { LogDirectory = null };
            var provider = new LogDirectoryProvider(configuration, configPath);

            var expected = ConfigurationPersistenceService.GetDefaultLogDirectoryPath(configPath);
            Assert.Equal(expected, provider.LogDirectory);
            Assert.Equal(Path.Combine(configDirectory, "logs"), provider.LogDirectory);
            Assert.True(Directory.Exists(provider.LogDirectory));
        }
        finally
        {
            DeleteDirectory(configDirectory);
        }
    }

    private static string CreateTempDirectoryPath()
        => Path.Combine(Path.GetTempPath(), $"phantom-logdir-{System.Guid.NewGuid():N}");

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
