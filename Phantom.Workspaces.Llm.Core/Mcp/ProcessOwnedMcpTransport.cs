using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using Phantom.Workspaces.Llm.Processes;

namespace Phantom.Workspaces.Llm.Mcp;

/// <summary>
/// MCP <see cref="ITransport"/> that delegates <see cref="SessionId"/>, <see cref="MessageReader"/>,
/// and <see cref="SendMessageAsync"/> to the SDK-owned stream transport while coupling its disposal
/// to the executor-owned <see cref="IProcessHandle"/> (issue #1477). Disposing closes the SDK
/// transport (which closes stdin), disposes the process handle (killing the tree), and awaits the
/// stderr drainer and exit monitor so the caller does not race the OS pipes.
/// </summary>
internal sealed class ProcessOwnedMcpTransport : ITransport
{
    private readonly ITransport inner;
    private readonly IProcessHandle handle;
    private readonly ProcessExecutorBackedClientTransport.StderrDrainer stderrDrainer;
    private readonly CancellationTokenSource drainCts;
    private readonly Task<ProcessExitResult> exitTask;
    private readonly string name;
    private readonly ILogger logger;
    private readonly Channel<JsonRpcMessage> messages = Channel.CreateUnbounded<JsonRpcMessage>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
    private readonly Task messagePump;
    private readonly object cleanupGate = new();
    private readonly HashSet<Task> reportedCleanupFailures = [];
    private Task? innerDisposal;
    private Task? processTermination;
    private Task? processDisposal;
    private ProcessExitResult? completedExitResult;
    private int closed;
    private int disposed;

    public ProcessOwnedMcpTransport(
        ITransport inner,
        IProcessHandle handle,
        ProcessExecutorBackedClientTransport.StderrDrainer stderrDrainer,
        CancellationTokenSource drainCts,
        Task<ProcessExitResult> exitTask,
        string name,
        ILogger? logger = null)
    {
        this.inner = inner;
        this.handle = handle;
        this.stderrDrainer = stderrDrainer;
        this.drainCts = drainCts;
        this.exitTask = exitTask;
        this.name = name;
        this.logger = logger ?? NullLogger.Instance;
        this.messagePump = this.PumpMessagesAsync();
    }

    /// <inheritdoc/>
    public string? SessionId => inner.SessionId;

    /// <inheritdoc/>
    public ChannelReader<JsonRpcMessage> MessageReader => messages.Reader;

    /// <inheritdoc/>
    public async Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        var sendTask = SendInnerAsync(message, cancellationToken);
        await Task.WhenAny(sendTask).ConfigureAwait(false);
        if (!sendTask.IsCompletedSuccessfully)
        {
            Interlocked.Exchange(ref closed, 1);
            await CaptureFailureAsync(
                TerminateProcessOnceAsync(),
                "terminating the MCP stdio process tree after a send failure").ConfigureAwait(false);
            await CaptureFailureAsync(
                exitTask,
                "joining MCP process exit after a send failure").ConfigureAwait(false);
            await CaptureFailureAsync(
                stderrDrainer.PumpTask,
                "draining MCP stderr after a send failure").ConfigureAwait(false);
            await CaptureFailureAsync(
                DisposeProcessOnceAsync(),
                "disposing the MCP stdio process tree after a send failure").ConfigureAwait(false);
        }

        await sendTask.ConfigureAwait(false);
    }

    private async Task PumpMessagesAsync()
    {
        Exception? readerFailure = null;
        ProcessExitResult? exitResult = null;
        var stdoutEof = false;
        while (true)
        {
            var messageReady = inner.MessageReader.WaitToReadAsync(drainCts.Token).AsTask();
            var completed = await Task.WhenAny(messageReady, exitTask).ConfigureAwait(false);
            if (completed == exitTask)
            {
                exitResult = await CaptureExitResultAsync().ConfigureAwait(false);
                readerFailure = await DrainInnerReaderAsync().ConfigureAwait(false);
                break;
            }

            if (messageReady.IsCanceled)
            {
                readerFailure = new OperationCanceledException(
                    $"MCP stdio server '{name}' message reader was cancelled.");
                break;
            }
            if (messageReady.IsFaulted)
            {
                readerFailure = GetTaskException(messageReady);
                break;
            }
            if (!await messageReady.ConfigureAwait(false))
            {
                Interlocked.Exchange(ref closed, 1);
                stdoutEof = true;
                break;
            }

            if (!DrainAvailableMessages())
                return;
        }

        Interlocked.Exchange(ref closed, 1);
        var terminationFailure = await CaptureFailureAsync(
            TerminateProcessOnceAsync(),
            "terminating the MCP stdio process tree").ConfigureAwait(false);
        if (terminationFailure is not null)
        {
            await CaptureFailureAsync(
                CancelAsyncTask(drainCts),
                "cancelling MCP exit monitoring after process termination failed").ConfigureAwait(false);
        }
        exitResult ??= await CaptureExitResultAsync().ConfigureAwait(false);
        var drainerFailure = await CaptureFailureAsync(
            stderrDrainer.PumpTask,
            "draining MCP stderr").ConfigureAwait(false);
        var processFailure = await CaptureFailureAsync(
            DisposeProcessOnceAsync(),
            "disposing the MCP stdio process tree").ConfigureAwait(false);
        var innerFailure = await CaptureFailureAsync(
            DisposeInnerOnceAsync(),
            "disposing the MCP SDK transport").ConfigureAwait(false);

        var failure = readerFailure
                      ?? terminationFailure
                      ?? processFailure
                      ?? innerFailure
                      ?? drainerFailure;
        if (failure is null && exitTask.IsCanceled)
        {
            failure = new OperationCanceledException(
                $"MCP stdio server '{name}' exit monitoring was cancelled.");
        }
        else if (failure is null && exitTask.IsFaulted)
        {
            failure = GetTaskException(exitTask);
        }
        else if (failure is null && !stdoutEof && exitResult is { ExitCode: not 0 } result)
        {
            var diagnostic = stderrDrainer.SnapshotRolling();
            var suffix = string.IsNullOrWhiteSpace(diagnostic) ? string.Empty : $" Stderr: {diagnostic}";
            failure = new IOException(
                $"MCP stdio server '{name}' exited prematurely with code {result.ExitCode}.{suffix}");
        }

        messages.Writer.TryComplete(failure);
    }

    private bool DrainAvailableMessages()
    {
        while (inner.MessageReader.TryRead(out var message))
        {
            if (!messages.Writer.TryWrite(message))
                return false;
        }

        return true;
    }

    private async Task<Exception?> DrainInnerReaderAsync()
    {
        while (true)
        {
            if (!DrainAvailableMessages())
                return null;

            var messageReady = inner.MessageReader.WaitToReadAsync(drainCts.Token).AsTask();
            await Task.WhenAny(messageReady).ConfigureAwait(false);
            if (messageReady.IsCanceled)
            {
                return new OperationCanceledException(
                    $"MCP stdio server '{name}' message reader was cancelled.");
            }
            if (messageReady.IsFaulted)
            {
                return GetTaskException(messageReady);
            }
            if (!await messageReady.ConfigureAwait(false))
            {
                return null;
            }
        }
    }

    private async Task<ProcessExitResult?> CaptureExitResultAsync()
    {
        await Task.WhenAny(exitTask).ConfigureAwait(false);
        if (exitTask.IsCompletedSuccessfully)
        {
            var result = await exitTask.ConfigureAwait(false);
            completedExitResult = result;
            return result;
        }

        return null;
    }

    private void ThrowIfClosed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Volatile.Read(ref closed) == 0 && !exitTask.IsCompleted)
            return;

        var result = completedExitResult;
        if (result is { ExitCode: not 0 })
        {
            var diagnostic = stderrDrainer.SnapshotRolling();
            var suffix = string.IsNullOrWhiteSpace(diagnostic) ? string.Empty : $" Stderr: {diagnostic}";
            throw new IOException(
                $"MCP stdio server '{name}' exited prematurely with code {result.ExitCode}.{suffix}");
        }

        throw new IOException($"MCP stdio server '{name}' transport is closed.");
    }

    private async Task SendInnerAsync(
        JsonRpcMessage message,
        CancellationToken cancellationToken) =>
        await inner.SendMessageAsync(message, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        Interlocked.Exchange(ref closed, 1);
        var innerFailure = await CaptureFailureAsync(
            DisposeInnerOnceAsync(),
            "disposing the MCP SDK transport").ConfigureAwait(false);
        var terminationFailure = await CaptureFailureAsync(
            TerminateProcessOnceAsync(),
            "terminating the MCP stdio process tree").ConfigureAwait(false);
        Exception? cancellationFailure = null;
        if (terminationFailure is not null)
        {
            cancellationFailure = await CaptureFailureAsync(
                CancelAsyncTask(drainCts),
                "cancelling MCP pipe drains after process termination failed").ConfigureAwait(false);
        }
        var exitFailure = await CaptureFailureAsync(
            exitTask,
            "joining MCP process exit").ConfigureAwait(false);
        var drainerFailure = await CaptureFailureAsync(
            stderrDrainer.PumpTask,
            "draining MCP stderr").ConfigureAwait(false);
        var processFailure = await CaptureFailureAsync(
            DisposeProcessOnceAsync(),
            "disposing the MCP stdio process tree").ConfigureAwait(false);
        cancellationFailure ??= await CaptureFailureAsync(
            CancelAsyncTask(drainCts),
            "cancelling MCP pipe drains").ConfigureAwait(false);
        var messagePumpFailure = await CaptureFailureAsync(
            messagePump,
            "joining the MCP message reader").ConfigureAwait(false);
        messages.Writer.TryComplete();
        drainCts.Dispose();

        var primaryFailure = innerFailure
                             ?? terminationFailure
                             ?? processFailure
                             ?? cancellationFailure
                             ?? drainerFailure
                             ?? exitFailure
                             ?? messagePumpFailure;
        if (primaryFailure is not null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
    }

    private Task DisposeInnerOnceAsync()
    {
        lock (cleanupGate)
        {
            return innerDisposal ??= DisposeAsyncCore(inner);
        }
    }

    private Task DisposeProcessOnceAsync()
    {
        lock (cleanupGate)
        {
            return processDisposal ??= DisposeAsyncCore(handle);
        }
    }

    private Task TerminateProcessOnceAsync()
    {
        lock (cleanupGate)
        {
            return processTermination ??= TerminateAsyncCore(handle);
        }
    }

    private static async Task DisposeAsyncCore(IAsyncDisposable disposable) =>
        await disposable.DisposeAsync().ConfigureAwait(false);

    private static async Task TerminateAsyncCore(IProcessHandle processHandle) =>
        await processHandle.TerminateAsync().ConfigureAwait(false);

    private static async Task CancelAsyncTask(CancellationTokenSource cancellation) =>
        await cancellation.CancelAsync().ConfigureAwait(false);

    private async Task<Exception?> CaptureFailureAsync(Task task, string operation)
    {
        await Task.WhenAny(task).ConfigureAwait(false);
        if (!task.IsFaulted)
            return null;

        var failure = GetTaskException(task);
        lock (cleanupGate)
        {
            if (!reportedCleanupFailures.Add(task))
                return failure;
        }

        logger.LogWarning(
            "Secondary cleanup failure while {Operation} for MCP stdio server '{Name}': {FailureType}.",
            operation,
            name,
            failure.GetType().Name);
        return failure;
    }

    private static Exception GetTaskException(Task task) =>
        task.Exception?.InnerException
        ?? new IOException("MCP stdio transport failed.");
}
