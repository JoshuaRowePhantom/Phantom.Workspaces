using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Services.Logging;

namespace Phantom.Workspaces.Web.Server.Tests;

/// <summary>
/// Covers #1095: log-directory resolution and file logging for hosts outside the main
/// <c>WorkspacesConfiguration</c> path (the standalone Web.Server / CLI executables and test hosts).
/// </summary>
public sealed class HostFileLoggingTests
{
    [Fact]
    public void WebServerHost_ResolvesLogDirectory_FromContentRootNotWorkspacesConfiguration()
    {
        var contentRoot = CreateTempDirectoryPath();
        try
        {
            // No explicit directory and no environment override: the host derives its log directory
            // from its own content root — never from a WorkspacesConfiguration document.
            var resolved = HostLogDirectoryResolver.Resolve(
                contentRoot,
                explicitDirectory: null,
                environmentReader: _ => null);

            Assert.Equal(Path.Combine(contentRoot, "logs"), resolved);
            Assert.True(Directory.Exists(resolved));
        }
        finally
        {
            DeleteDirectory(contentRoot);
        }
    }

    [Fact]
    public void WebServerHost_EnvironmentOverride_TakesPrecedenceOverContentRoot()
    {
        var contentRoot = CreateTempDirectoryPath();
        var overrideDirectory = CreateTempDirectoryPath();
        try
        {
            var resolved = HostLogDirectoryResolver.Resolve(
                contentRoot,
                explicitDirectory: null,
                environmentReader: name =>
                    name == HostLogDirectoryResolver.LogDirectoryEnvironmentVariable
                        ? overrideDirectory
                        : null);

            Assert.Equal(overrideDirectory, resolved);
            Assert.True(Directory.Exists(overrideDirectory));
        }
        finally
        {
            DeleteDirectory(contentRoot);
            DeleteDirectory(overrideDirectory);
        }
    }

    [Fact]
    public void WebServerHost_RegistersRollingFileProvider_WritesEntriesToLogDirectory()
    {
        var contentRoot = CreateTempDirectoryPath();
        try
        {
            var logDirectory = HostLogDirectoryResolver.Resolve(
                contentRoot,
                explicitDirectory: null,
                environmentReader: _ => null);

            using (var loggerFactory = HostFileLoggerFactory.Create(logDirectory))
            {
                var logger = loggerFactory.CreateLogger("WebServerHost");
                logger.LogInformation("standalone web server entry");
            }

            var file = Assert.Single(Directory.GetFiles(logDirectory, "phantom-workspaces-*.log"));
            var content = ReadAllTextShared(file);
            Assert.Contains("standalone web server entry", content, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(contentRoot);
        }
    }

    [Fact]
    public void WebServerHost_PrunesFilesOlderThanSevenDays_DeletesExpiredFiles()
    {
        var logDirectory = CreateTempDirectoryPath();
        Directory.CreateDirectory(logDirectory);
        try
        {
            var expired = Path.Combine(
                logDirectory,
                $"phantom-workspaces-{DateTime.UtcNow.AddDays(-10):yyyyMMdd}.log");
            var retained = Path.Combine(
                logDirectory,
                $"phantom-workspaces-{DateTime.UtcNow.AddDays(-1):yyyyMMdd}.log");
            File.WriteAllText(expired, "old");
            File.WriteAllText(retained, "recent");

            // The shared provider prunes on construction using the default 7-day retention.
            using (HostFileLoggerFactory.Create(logDirectory))
            {
                Assert.False(File.Exists(expired));
                Assert.True(File.Exists(retained));
            }
        }
        finally
        {
            DeleteDirectory(logDirectory);
        }
    }

    [Fact]
    public void TestHost_ResolvesIsolatedLogDirectory_PerRun()
    {
        var firstRoot = CreateTempDirectoryPath();
        var secondRoot = CreateTempDirectoryPath();
        try
        {
            var first = HostLogDirectoryResolver.Resolve(firstRoot, environmentReader: _ => null);
            var second = HostLogDirectoryResolver.Resolve(secondRoot, environmentReader: _ => null);

            Assert.NotEqual(first, second);
            Assert.True(Directory.Exists(first));
            Assert.True(Directory.Exists(second));
        }
        finally
        {
            DeleteDirectory(firstRoot);
            DeleteDirectory(secondRoot);
        }
    }

    private static string CreateTempDirectoryPath()
        => Path.Combine(Path.GetTempPath(), $"phantom-hostlog-{Guid.NewGuid():N}");

    // The rolling provider keeps its file handle open with write access, so read with a shared handle.
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
            // (the factory does not own instance-registered providers), so best-effort cleanup of the
            // temp directory is sufficient for the test.
        }
    }
}
