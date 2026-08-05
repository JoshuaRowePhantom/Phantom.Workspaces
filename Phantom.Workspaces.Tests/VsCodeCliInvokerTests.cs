using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Services.Notifications;
using Phantom.Workspaces.Tools;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class VsCodeCliInvokerTests
{
    private sealed class RecordingNotificationService : INotificationService
    {
        private readonly List<Notification> calls = [];
        public IReadOnlyList<Notification> Calls
        {
            get { lock (this.calls) { return this.calls.ToArray(); } }
        }

        public IReadOnlyList<NotificationEntry> Notifications => [];
        public bool HasActiveRun => false;
#pragma warning disable CS0067
        public event System.EventHandler? NotificationsChanged;
#pragma warning restore CS0067

        public void Notify(Notification notification)
        {
            lock (this.calls) { this.calls.Add(notification); }
        }
        public void Remove(string tabId) { }
        public void MarkRead(string tabId) { }
    }

    [Fact]
    public async Task VsCodeCliInvoker_SuccessfulInvocation_LogsCommandLineAndOutputAndExitCode()
    {
        var logger = new TestLogger<VsCodeCliInvokerTests>();
        var invoker = new VsCodeCliInvoker(
            notificationService: null,
            logger: logger,
            processRunner: (_, _) => Task.FromResult(new ProcessResult(0, "hello stdout", "hello stderr", "hello stdout\nhello stderr")));

        var result = await invoker.RunAsync(
            "code",
            "tunnel status",
            operationDescription: "vscode tunnel status",
            VsCodeCliReporting.LogOnly, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello stdout", result.StandardOut);
        Assert.Contains(logger.Entries, e =>
            e.Message.Contains("tunnel", System.StringComparison.OrdinalIgnoreCase)
            && e.Message.Contains("hello stdout", System.StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Message.Contains("hello stderr", System.StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Message.Contains("0", System.StringComparison.Ordinal));
    }

    [Fact]
    public async Task VsCodeCliInvoker_NonZeroExit_LogsWarningWithStderr()
    {
        var logger = new TestLogger<VsCodeCliInvokerTests>();
        var invoker = new VsCodeCliInvoker(
            notificationService: null,
            logger: logger,
            processRunner: (_, _) => Task.FromResult(new ProcessResult(2, "", "boom stderr", "boom stderr")));

        await invoker.RunAsync(
            "code",
            "tunnel status",
            operationDescription: "vscode tunnel status",
            VsCodeCliReporting.LogOnly, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("boom stderr", System.StringComparison.Ordinal));
    }

    [Fact]
    public async Task VsCodeCliInvoker_NonZeroExit_NotifiesUserWithStdoutAndStderr()
    {
        var logger = new TestLogger<VsCodeCliInvokerTests>();
        var notifier = new RecordingNotificationService();
        var invoker = new VsCodeCliInvoker(
            notificationService: notifier,
            logger: logger,
            processRunner: (_, _) => Task.FromResult(new ProcessResult(3, "captured-stdout", "captured-stderr", "captured-stdout\ncaptured-stderr")));

        await invoker.RunAsync(
            "code",
            "tunnel status",
            operationDescription: "vscode tunnel status",
            VsCodeCliReporting.LogAndReportOnFailure, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(notifier.Calls);
        Assert.Contains("captured-stdout", notifier.Calls[0].Description);
        Assert.Contains("captured-stderr", notifier.Calls[0].Description);
        Assert.Contains("3", notifier.Calls[0].Heading);
    }

    [Fact]
    public async Task VsCodeCliInvoker_Success_DoesNotNotifyWhenReportOnFailureOnly()
    {
        var notifier = new RecordingNotificationService();
        var invoker = new VsCodeCliInvoker(
            notificationService: notifier,
            logger: null,
            processRunner: (_, _) => Task.FromResult(new ProcessResult(0, "ok", string.Empty, "ok")));

        await invoker.RunAsync(
            "code",
            "tunnel status",
            operationDescription: "vscode tunnel status",
            VsCodeCliReporting.LogAndReportOnFailure, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(notifier.Calls);
    }

    [Fact]
    public async Task VsCodeCliInvoker_LongRunningInvocation_DoesNotBlockCallingThread()
    {
        // Simulate a long-running CLI: only completes when we manually resolve the TCS.
        // Assert that RunAsync returns an incomplete Task synchronously and does not marshal
        // back to any calling synchronization context (we do not await it under one).
        var tcs = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoker = new VsCodeCliInvoker(
            notificationService: null,
            logger: null,
            processRunner: (_, _) => tcs.Task);

        var invokeTask = invoker.RunAsync(
            "code",
            "tunnel status",
            operationDescription: "vscode tunnel status",
            VsCodeCliReporting.LogOnly, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(invokeTask.IsCompleted);
        tcs.SetResult(new ProcessResult(0, "done", string.Empty, "done"));
        var result = await invokeTask;
        Assert.Equal(0, result.ExitCode);
    }
}
