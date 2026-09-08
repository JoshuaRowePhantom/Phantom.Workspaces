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
    public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        ThrowIfExited();
        return inner.SendMessageAsync(message, cancellationToken);
    }

    private async Task PumpMessagesAsync()
    {
        try
        {
            while (true)
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

                if (!await messageReady.ConfigureAwait(false))
                {
                    await CompleteFromExitAsync().ConfigureAwait(false);
                    return;
                }

                while (inner.MessageReader.TryRead(out var message))
                    await messages.Writer.WriteAsync(message, drainCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            messages.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            messages.Writer.TryComplete(ex);
        }
    }

    private async Task CompleteFromExitAsync()
    {
        var result = await exitTask.ConfigureAwait(false);
        while (await inner.MessageReader.WaitToReadAsync(drainCts.Token).ConfigureAwait(false))
        {
            while (inner.MessageReader.TryRead(out var message))
                await messages.Writer.WriteAsync(message, drainCts.Token).ConfigureAwait(false);
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

    private void ThrowIfExited()
    {
        if (!exitTask.IsCompletedSuccessfully || exitTask.Result.ExitCode == 0)
            return;

        var diagnostic = stderrDrainer.SnapshotRolling();
        var suffix = string.IsNullOrWhiteSpace(diagnostic) ? string.Empty : $" Stderr: {diagnostic}";
        throw new IOException(
            $"MCP stdio server '{name}' exited prematurely with code {exitTask.Result.ExitCode}.{suffix}");
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        try
        {
            await inner.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            await handle.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            await drainCts.CancelAsync().ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            await Task.WhenAll(stderrDrainer.PumpTask, exitTask, messagePump).ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            drainCts.Dispose();
        }
    }
}
