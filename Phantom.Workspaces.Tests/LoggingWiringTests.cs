using System;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Services.Logging;

namespace Phantom.Workspaces.Tests;

public sealed class LoggingWiringTests
{
    [Fact]
    public void LoggerFactory_ResolvesFileProvider_NotNullLogger_WhenConfigured()
    {
        var directory = CreateTempDirectoryPath();
        var configuration = new WorkspacesConfiguration { LogDirectory = directory };
        var logDirectoryProvider = new LogDirectoryProvider(configuration, configurationPath: null);
        var loggerFactory = LoggingBootstrap.CreateLoggerFactory(logDirectoryProvider);
        try
        {
            var logger = loggerFactory.CreateLogger("StartupCategory");
            Assert.IsNotType<NullLogger>(logger);

            logger.LogInformation("startup path emitted this line");
            loggerFactory.Dispose();

            var files = Directory.GetFiles(directory, "phantom-workspaces-*.log");
            var file = Assert.Single(files);
            Assert.Contains(
                "startup path emitted this line",
                ReadAllTextShared(file),
                StringComparison.Ordinal);
        }
        finally
        {
            loggerFactory.Dispose();
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void LoggingWiring_UsesSingleLogDirectory_ForGuiAndEmbeddedWebHost()
    {
        var directory = CreateTempDirectoryPath();
        var configuration = new WorkspacesConfiguration { LogDirectory = directory };

        // The embedded host forwards into the same factory rather than opening a second file.
        var logDirectoryProvider = new LogDirectoryProvider(configuration, configurationPath: null);
        var guiFactory = LoggingBootstrap.CreateLoggerFactory(logDirectoryProvider);
        var webHostProvider = new ForwardingLoggerProvider(guiFactory);
        try
        {
            guiFactory.CreateLogger("Gui").LogInformation("from the gui");
            webHostProvider.CreateLogger("WebHost").LogInformation("from the web host");
            guiFactory.Dispose();
            webHostProvider.Dispose();

            var files = Directory.GetFiles(directory, "phantom-workspaces-*.log");
            var file = Assert.Single(files);
            var content = ReadAllTextShared(file);
            Assert.Contains("from the gui", content, StringComparison.Ordinal);
            Assert.Contains("from the web host", content, StringComparison.Ordinal);
            Assert.Equal(1, content.Split("from the web host", StringSplitOptions.None).Length - 1);
        }
        finally
        {
            guiFactory.Dispose();
            webHostProvider.Dispose();
            DeleteDirectory(directory);
        }
    }

    private static string CreateTempDirectoryPath()
        => Path.Combine(AppContext.BaseDirectory, "logging-wiring-tests", Guid.NewGuid().ToString("N"));

    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
