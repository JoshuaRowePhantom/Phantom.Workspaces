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

        // Both the GUI startup path and the embedded web host obtain the directory from the one
        // WorkspacesConfiguration-driven resolver, so neither can diverge.
        var logDirectoryProvider = new LogDirectoryProvider(configuration, configurationPath: null);
        var guiFactory = LoggingBootstrap.CreateLoggerFactory(logDirectoryProvider);
        var webHostProvider = new RollingFileLoggerProvider(
            logDirectoryProvider.LogDirectory,
            LoggingBootstrap.DefaultRetention);
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
        }
        finally
        {
            guiFactory.Dispose();
            webHostProvider.Dispose();
            DeleteDirectory(directory);
        }
    }

    private static string CreateTempDirectoryPath()
        => Path.Combine(Path.GetTempPath(), $"phantom-logwiring-{Guid.NewGuid():N}");

    // The rolling file provider registered on a LoggerFactory keeps its file handle open (the
    // factory does not own instance-registered providers), so read with a shared handle.
    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // The rolling file provider registered on the LoggerFactory keeps its file handle open
            // for the process lifetime (the factory does not own instance-registered providers), so
            // best-effort cleanup of the temp directory is sufficient for the test.
        }
    }
}
