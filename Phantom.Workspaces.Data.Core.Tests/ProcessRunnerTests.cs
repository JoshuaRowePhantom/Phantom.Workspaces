using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces;

namespace Phantom.Workspaces.Data.Tests;

public sealed class ProcessRunnerTests
{
    // -----------------------------------------------------------------------
    // Basic result capture
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunProcessAsync_ReturnsZeroExitCode_ForSuccessfulProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await ProcessRunner.RunProcessAsync(new RunProcessParameters(
            Command: "cmd.exe",
            Arguments: ["/c", "exit", "0"]));

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task RunProcessAsync_ReturnsNonZeroExitCode_ForFailingProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await ProcessRunner.RunProcessAsync(new RunProcessParameters(
            Command: "cmd.exe",
            Arguments: ["/c", "exit", "42"]));

        Assert.Equal(42, result.ExitCode);
    }

    [Fact]
    public async Task RunProcessAsync_CapturesStandardOut()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await ProcessRunner.RunProcessAsync(new RunProcessParameters(
            Command: "cmd.exe",
            Arguments: ["/c", "echo hello"]));

        Assert.Contains("hello", result.StandardOut);
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public async Task RunProcessAsync_CapturesStandardError()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await ProcessRunner.RunProcessAsync(new RunProcessParameters(
            Command: "cmd.exe",
            Arguments: ["/c", "echo error 1>&2"]));

        Assert.Contains("error", result.StandardError);
        Assert.Empty(result.StandardOut);
    }

    [Fact]
    public async Task RunProcessAsync_StandardOutAndError_ContainsBothStreams()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await ProcessRunner.RunProcessAsync(new RunProcessParameters(
            Command: "cmd.exe",
            Arguments: ["/c", "echo out && echo err 1>&2"]));

        Assert.Contains("out", result.StandardOutAndError);
        Assert.Contains("err", result.StandardOutAndError);
        Assert.Contains("out", result.StandardOut);
        Assert.Contains("err", result.StandardError);
    }

    // -----------------------------------------------------------------------
    // Cancellation
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunProcessAsync_ThrowsOperationCanceledException_WhenCancelledBeforeProcessExits()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await ProcessRunner.RunProcessAsync(
                new RunProcessParameters(
                    Command: "cmd.exe",
                    Arguments: ["/c", "ping", "-n", "9999", "127.0.0.1"]),
                cts.Token));
    }

    [Fact]
    public async Task RunProcessAsync_ThrowsOperationCanceledException_PreservesOriginalToken()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await ProcessRunner.RunProcessAsync(
                new RunProcessParameters(
                    Command: "cmd.exe",
                    Arguments: ["/c", "ping", "-n", "9999", "127.0.0.1"]),
                cts.Token));

        Assert.Equal(cts.Token, ex.CancellationToken);
    }

    // -----------------------------------------------------------------------
    // Timeout
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunProcessAsync_ThrowsTimeoutException_WhenTimeoutExpires()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var parameters = new RunProcessParameters(
            Command: "cmd.exe",
            Arguments: ["/c", "ping", "-n", "9999", "127.0.0.1"],
            Timeout: TimeSpan.FromMilliseconds(1));

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await ProcessRunner.RunProcessAsync(parameters));
    }

    // -----------------------------------------------------------------------
    // KillOnClose / Windows Job Object
    // -----------------------------------------------------------------------

    [Fact]
    [SupportedOSPlatform("windows")]
    public void AssignToWindowsJobObject_AssignsProcessToJobObject()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c pause")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Failed to start process.");

        try
        {
            ProcessRunner.AssignToWindowsJobObject(process);
            Assert.True(IsProcessInAnyJob(process.Handle));
        }
        finally
        {
            process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task RunProcessAsync_KillOnCloseKillTree_CompletesSuccessfully_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await ProcessRunner.RunProcessAsync(new RunProcessParameters(
            Command: "cmd.exe",
            Arguments: ["/c", "exit", "0"],
            KillOnClose: KillOnCloseAction.KillTree));

        Assert.Equal(0, result.ExitCode);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static extern bool IsProcessInJob(
        IntPtr processHandle,
        IntPtr jobHandle,
        [MarshalAs(UnmanagedType.Bool)] out bool result);

    [SupportedOSPlatform("windows")]
    private static bool IsProcessInAnyJob(IntPtr processHandle)
    {
        IsProcessInJob(processHandle, IntPtr.Zero, out var result);
        return result;
    }

    // -----------------------------------------------------------------------
    // RunAndLogAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunAndLogAsync_NoLogCall_WhenExitCodeIsZero()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var logger = new FakeLogger();
        await ProcessRunner.RunAndLogAsync(
            new RunProcessParameters("cmd.exe", ["/c", "exit", "0"]),
            logger);

        Assert.Empty(logger.Logs);
    }

    [Fact]
    public async Task RunAndLogAsync_LogsWarning_WhenExitCodeIsNonZero()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var logger = new FakeLogger();
        await ProcessRunner.RunAndLogAsync(
            new RunProcessParameters("cmd.exe", ["/c", "echo failed-output && exit 1"]),
            logger);

        var entry = Assert.Single(logger.Logs);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("failed-output", entry.Message);
    }

    [Fact]
    public async Task RunAndLogAsync_IncludesOperationDescription_InLogMessage()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var logger = new FakeLogger();
        await ProcessRunner.RunAndLogAsync(
            new RunProcessParameters("cmd.exe", ["/c", "exit", "1"]),
            logger,
            operationDescription: "my test operation");

        var entry = Assert.Single(logger.Logs);
        Assert.Contains("my test operation", entry.Message);
    }

    [Fact]
    public async Task RunAndLogAsync_ReturnsResult_WhenExitCodeIsNonZero()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var logger = new FakeLogger();
        var result = await ProcessRunner.RunAndLogAsync(
            new RunProcessParameters("cmd.exe", ["/c", "exit", "42"]),
            logger);

        Assert.Equal(42, result.ExitCode);
    }

    [Fact]
    public async Task RunAndLogAsync_ProcessSucceeds_LogsStdoutAtDebugLevel()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var logger = new FakeLogger();
        await ProcessRunner.RunAndLogAsync(
            new RunProcessParameters("cmd.exe", ["/c", "echo test-output && exit 0"]),
            logger);

        var entry = Assert.Single(logger.Logs);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Contains("test-output", entry.Message);
    }

    [Fact]
    public async Task RunAndLogAsync_ProcessSucceeds_LogsStderrAtDebugLevel()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var logger = new FakeLogger();
        await ProcessRunner.RunAndLogAsync(
            new RunProcessParameters("cmd.exe", ["/c", "echo test-error 1>&2 && exit 0"]),
            logger);

        var entry = Assert.Single(logger.Logs);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Contains("test-error", entry.Message);
    }

    [Fact]
    public async Task RunAndLogAsync_ProcessTimesOut_LogsOutputAtErrorLevel()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var logger = new FakeLogger();
        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await ProcessRunner.RunAndLogAsync(
                new RunProcessParameters(
                    "cmd.exe",
                    ["/c", "echo timed-out && ping -n 9999 127.0.0.1"],
                    Timeout: TimeSpan.FromMilliseconds(100)),
                logger));

        var entry = Assert.Single(logger.Logs);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("timed out", entry.Message);
        Assert.Contains("timed-out", entry.Message);
    }

    [Fact]
    public async Task RunProcessAsync_TimeoutExpires_ThrowsTimeoutException()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var parameters = new RunProcessParameters(
            Command: "cmd.exe",
            Arguments: ["/c", "ping", "-n", "9999", "127.0.0.1"],
            Timeout: TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAsync<TimeoutException>(async () =>
            await ProcessRunner.RunProcessAsync(parameters));

        Assert.Contains("did not complete within", ex.Message);
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
