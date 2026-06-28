using Microsoft.Extensions.Logging;
using Phantom.Workspaces;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class GitHubAuthTokenResolverTests
{
    [Fact]
    public void ResolveFromCli_ReturnsToken_WhenProcessSucceeds()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var logger = new FakeLogger();

        var result = GitHubAuthTokenResolver.ResolveFromCliCore(
            logger,
            new RunProcessParameters("cmd.exe", ["/c", "echo", "ghs_testtoken"]));

        Assert.Equal("ghs_testtoken", result);
        Assert.Empty(logger.Logs);
    }

    [Fact]
    public void ResolveFromCli_ReturnsNull_WhenProcessExitsNonZero()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var logger = new FakeLogger();

        var result = GitHubAuthTokenResolver.ResolveFromCliCore(
            logger,
            new RunProcessParameters("cmd.exe", ["/c", "exit", "1"]));

        Assert.Null(result);
    }

    [Fact]
    public void ResolveFromCli_LogsWarning_WhenProcessExitsNonZero()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var logger = new FakeLogger();

        GitHubAuthTokenResolver.ResolveFromCliCore(
            logger,
            new RunProcessParameters("cmd.exe", ["/c", "exit", "1"]));

        var entry = Assert.Single(logger.Logs);
        Assert.Equal(LogLevel.Warning, entry.Level);
    }

    private sealed class FakeLogger : ILogger
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
