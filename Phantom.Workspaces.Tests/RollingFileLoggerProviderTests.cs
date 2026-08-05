using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Phantom.Workspaces.Services.Logging;

namespace Phantom.Workspaces.Tests;

public sealed class RollingFileLoggerProviderTests
{
    [Fact]
    public void RollingFileLogger_WritesEntry_CreatesFileInResolvedLogDirectory()
    {
        var directory = CreateTempDirectoryPath();
        var provider = new RollingFileLoggerProvider(directory, TimeSpan.FromDays(7));
        try
        {
            var logger = provider.CreateLogger("TestCategory");
            logger.LogInformation("hello from the rolling logger");
            provider.Dispose();

            var files = Directory.GetFiles(directory, "phantom-workspaces-*.log");
            var file = Assert.Single(files);
            var content = File.ReadAllText(file);
            Assert.Contains("hello from the rolling logger", content, StringComparison.Ordinal);
            Assert.Equal(directory, Path.GetDirectoryName(file));
        }
        finally
        {
            provider.Dispose();
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void RollingFileLogger_PrunesFilesOlderThanSevenDays_DeletesExpiredFiles()
    {
        var directory = CreateTempDirectoryPath();
        Directory.CreateDirectory(directory);
        RollingFileLoggerProvider? provider = null;
        try
        {
            var expired = Path.Combine(
                directory,
                $"phantom-workspaces-{DateTime.UtcNow.AddDays(-10):yyyyMMdd}.log");
            var retained = Path.Combine(
                directory,
                $"phantom-workspaces-{DateTime.UtcNow.AddDays(-1):yyyyMMdd}.log");
            File.WriteAllText(expired, "old");
            File.WriteAllText(retained, "recent");

            // Construction prunes on startup.
            provider = new RollingFileLoggerProvider(directory, TimeSpan.FromDays(7));

            Assert.False(File.Exists(expired));
            Assert.True(File.Exists(retained));
        }
        finally
        {
            provider?.Dispose();
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void RollingFileLogger_RollsOnDateChange_RetainsSevenDaysOfFiles()
    {
        var directory = CreateTempDirectoryPath();
        var start = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(start);
        var provider = new RollingFileLoggerProvider(directory, TimeSpan.FromDays(7), timeProvider);
        try
        {
            var logger = provider.CreateLogger("Cat");

            logger.LogInformation("day one");
            var firstFile = Path.Combine(directory, "phantom-workspaces-20240101.log");
            Assert.True(File.Exists(firstFile));

            // Advance beyond the retention window and write again: a new dated file is created and
            // the older, now-expired file is pruned.
            timeProvider.Advance(TimeSpan.FromDays(8));
            logger.LogInformation("day nine");
            provider.Dispose();

            var secondFile = Path.Combine(directory, "phantom-workspaces-20240109.log");
            Assert.True(File.Exists(secondFile));
            Assert.False(File.Exists(firstFile));

            var files = Directory.GetFiles(directory, "phantom-workspaces-*.log");
            Assert.Single(files);
        }
        finally
        {
            provider.Dispose();
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void RollingFileLogger_WarningAndError_AreWrittenToFile()
    {
        var directory = CreateTempDirectoryPath();
        var provider = new RollingFileLoggerProvider(directory, TimeSpan.FromDays(7));
        try
        {
            var logger = provider.CreateLogger("Cat");

            logger.LogWarning("a warning occurred");
            logger.LogError(new InvalidOperationException("boom"), "an error occurred");
            provider.Dispose();

            var file = Directory.GetFiles(directory, "phantom-workspaces-*.log").Single();
            var content = File.ReadAllText(file);
            Assert.Contains("Warning", content, StringComparison.Ordinal);
            Assert.Contains("a warning occurred", content, StringComparison.Ordinal);
            Assert.Contains("Error", content, StringComparison.Ordinal);
            Assert.Contains("an error occurred", content, StringComparison.Ordinal);
            Assert.Contains("InvalidOperationException", content, StringComparison.Ordinal);
        }
        finally
        {
            provider.Dispose();
            DeleteDirectory(directory);
        }
    }

    private static string CreateTempDirectoryPath()
        => Path.Combine(Path.GetTempPath(), $"phantom-rollinglog-{Guid.NewGuid():N}");

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
