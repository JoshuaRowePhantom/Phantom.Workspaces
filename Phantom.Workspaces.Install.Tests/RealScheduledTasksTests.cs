using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Install;

namespace Phantom.Workspaces.Install.Tests;

public sealed class RealScheduledTasksTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public void Register_LogsWarning_WhenSchtasksExitsNonZero()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var logger = new FakeLogger<RealScheduledTasks>();
        var tasks = new RealScheduledTasks(logger);

        // Task names containing wildcard characters are rejected by schtasks.exe (non-zero exit).
        Assert.Throws<InvalidOperationException>(() =>
            tasks.Register(new ScheduledTaskDefinition
            {
                TaskName = "Invalid*Task?Name",
                ExecutablePath = @"C:\fake.exe",
                Arguments = [],
            }));

        Assert.Contains(logger.Logs, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Register_WhenSchtasksFails_IncludesStdErrInException()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var logger = new FakeLogger<RealScheduledTasks>();
        var tasks = new RealScheduledTasks(logger);

        // schtasks.exe rejects wildcard characters in the task name and emits an "ERROR:"
        // diagnostic line on STDERR. Issue #1298: that stderr text must be surfaced in the
        // thrown exception so update-settings failures are diagnosable.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            tasks.Register(new ScheduledTaskDefinition
            {
                TaskName = "Invalid*Task?Name",
                ExecutablePath = @"C:\fake.exe",
                Arguments = [],
            }));

        // Enriched message format: "schtasks failed (exit N) registering '<name>': <stderr>"
        // — the ": <non-empty>" suffix after the task name is the enrichment we require.
        Assert.Matches(@"registering 'Invalid\*Task\?Name':\s+\S", ex.Message);
    }

    private sealed class FakeLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Logs { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Logs.Add((logLevel, formatter(state, exception)));
        }
    }
}
