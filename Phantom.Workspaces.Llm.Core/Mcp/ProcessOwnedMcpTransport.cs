using System.Threading.Channels;
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
    private readonly Channel<JsonRpcMessage> messages = Channel.CreateUnbounded<JsonRpcMessage>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
    private readonly Task messagePump;
    private readonly object cleanupGate = new();
    private Task? innerDisposal;
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
        string name)
    {
        this.inner = inner;
        this.handle = handle;
        this.stderrDrainer = stderrDrainer;
        this.drainCts = drainCts;
        this.exitTask = exitTask;
        this.name = name;
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
            await ObserveAsync(DisposeProcessOnceAsync()).ConfigureAwait(false);
        }

        await sendTask.ConfigureAwait(false);
    }

    private async Task PumpMessagesAsync()
    {
        while (Volatile.Read(ref disposed) == 0)
        {
            if (exitTask.IsCompleted)
            {
                await CompleteFromExitAsync().ConfigureAwait(false);
                return;
            }

            var messageReady = inner.MessageReader.WaitToReadAsync(drainCts.Token).AsTask();
            var completed = await Task.WhenAny(messageReady, exitTask).ConfigureAwait(false);
            if (completed == exitTask)
            {
                await CompleteFromExitAsync().ConfigureAwait(false);
                return;
            }

            if (messageReady.IsCanceled)
            {
                messages.Writer.TryComplete();
                return;
            }
            if (messageReady.IsFaulted)
            {
                await ObserveAsync(DisposeProcessOnceAsync()).ConfigureAwait(false);
                messages.Writer.TryComplete(GetTaskException(messageReady));
                return;
            }
            if (!await messageReady.ConfigureAwait(false))
            {
                Interlocked.Exchange(ref closed, 1);
                await CompleteFromExitAsync().ConfigureAwait(false);
                return;
            }

            while (inner.MessageReader.TryRead(out var message))
            {
                if (!messages.Writer.TryWrite(message))
                    return;
            }
        }

        messages.Writer.TryComplete();
    }

    private async Task CompleteFromExitAsync()
    {
        Interlocked.Exchange(ref closed, 1);
        await Task.WhenAny(exitTask).ConfigureAwait(false);
        while (inner.MessageReader.TryRead(out var message))
        {
            if (!messages.Writer.TryWrite(message))
                return;
        }

        if (exitTask.IsCanceled)
        {
            await ObserveAsync(DisposeProcessOnceAsync()).ConfigureAwait(false);
            messages.Writer.TryComplete(new OperationCanceledException(
                $"MCP stdio server '{name}' exit monitoring was cancelled."));
            return;
        }
        if (exitTask.IsFaulted)
        {
            await ObserveAsync(DisposeProcessOnceAsync()).ConfigureAwait(false);
            messages.Writer.TryComplete(GetTaskException(exitTask));
            return;
        }

        var result = await exitTask.ConfigureAwait(false);
        completedExitResult = result;
        var processCleanup = DisposeProcessOnceAsync();
        await Task.WhenAny(processCleanup).ConfigureAwait(false);
        if (!processCleanup.IsCompletedSuccessfully)
        {
            messages.Writer.TryComplete(GetTaskException(processCleanup));
            return;
        }
        if (result.ExitCode == 0)
        {
            messages.Writer.TryComplete();
            return;
        }

        await stderrDrainer.PumpTask.ConfigureAwait(false);
        var diagnostic = stderrDrainer.SnapshotRolling();
        var suffix = string.IsNullOrWhiteSpace(diagnostic) ? string.Empty : $" Stderr: {diagnostic}";
        messages.Writer.TryComplete(new IOException(
            $"MCP stdio server '{name}' exited prematurely with code {result.ExitCode}.{suffix}"));
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
        try
        {
            await DisposeInnerOnceAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await DisposeProcessOnceAsync().ConfigureAwait(false);
            }
            finally
            {
                await drainCts.CancelAsync().ConfigureAwait(false);
                await ObserveAsync(stderrDrainer.PumpTask).ConfigureAwait(false);
                await ObserveAsync(exitTask).ConfigureAwait(false);
                await ObserveAsync(messagePump).ConfigureAwait(false);
                messages.Writer.TryComplete();
                drainCts.Dispose();
            }
        }
    }

    private Task DisposeInnerOnceAsync()
    {
        lock (cleanupGate)
        {
            return innerDisposal ??= inner.DisposeAsync().AsTask();
        }
    }

    private Task DisposeProcessOnceAsync()
    {
        lock (cleanupGate)
        {
            return processDisposal ??= handle.DisposeAsync().AsTask();
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        await Task.WhenAny(task).ConfigureAwait(false);
        _ = task.Exception;
    }

    private static Exception GetTaskException(Task task) =>
        task.Exception?.InnerException
        ?? new IOException("MCP stdio transport failed.");
}
