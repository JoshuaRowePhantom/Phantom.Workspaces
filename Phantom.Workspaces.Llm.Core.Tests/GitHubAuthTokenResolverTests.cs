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
        // Successful process execution logs at Debug level
        if (logger.Logs.Any())
        {
            Assert.All(logger.Logs, log => Assert.Equal(LogLevel.Debug, log.Level));
        }
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

    [Fact]
    public async Task ResolveFromCliCoreAsync_ReturnsToken_WhenCliSucceeds()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var logger = new FakeLogger();

        var result = await GitHubAuthTokenResolver.ResolveFromCliCoreAsync(
            logger,
            new RunProcessParameters("cmd.exe", ["/c", "echo", "ghs_asynctoken"]));

        Assert.Equal("ghs_asynctoken", result);
        if (logger.Logs.Any())
        {
            Assert.All(logger.Logs, log => Assert.Equal(LogLevel.Debug, log.Level));
        }
    }

    [Fact]
    public async Task ResolveFromCliCoreAsync_ReturnsNull_WhenCliFails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var logger = new FakeLogger();

        var result = await GitHubAuthTokenResolver.ResolveFromCliCoreAsync(
            logger,
            new RunProcessParameters("cmd.exe", ["/c", "exit", "1"]));

        Assert.Null(result);
    }
}
