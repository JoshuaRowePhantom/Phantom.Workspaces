using System;
using System.IO;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Services.Logging;

namespace Phantom.Workspaces.Agent.Cli.Tests;

/// <summary>
/// Covers #1095: the CLI console app resolves its own log directory (outside the main
/// <c>WorkspacesConfiguration</c> path) and writes a retained file log when file logging is
/// requested.
/// </summary>
public sealed class AgentCliFileLoggingTests
{
    [Fact]
    public void AgentCli_WhenFileLoggingRequested_WritesToResolvedLogDirectory()
    {
        var baseDirectory = CreateTempDirectoryPath();
        try
        {
            // The CLI resolves a log directory next to its executable base directory, independent of
            // any WorkspacesConfiguration document.
            var logDirectory = HostLogDirectoryResolver.Resolve(
                baseDirectory,
                explicitDirectory: null,
                environmentReader: _ => null);

            Assert.Equal(Path.Combine(baseDirectory, "logs"), logDirectory);

            using (var loggerFactory = HostFileLoggerFactory.Create(logDirectory))
            {
                var logger = loggerFactory.CreateLogger("Phantom.Workspaces.Llm.AgentCli");
                logger.LogInformation("cli file logging requested");
            }

            var file = Assert.Single(Directory.GetFiles(logDirectory, "phantom-workspaces-*.log"));
            var content = ReadAllTextShared(file);
            Assert.Contains("cli file logging requested", content, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(baseDirectory);
        }
    }

    private static string CreateTempDirectoryPath()
        => Path.Combine(Path.GetTempPath(), $"phantom-cli-hostlog-{Guid.NewGuid():N}");

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
