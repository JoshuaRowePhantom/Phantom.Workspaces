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
    private readonly Task exitMonitor;
    private int disposed;

    public ProcessOwnedMcpTransport(
        ITransport inner,
        IProcessHandle handle,
        ProcessExecutorBackedClientTransport.StderrDrainer stderrDrainer,
        CancellationTokenSource drainCts,
        Task exitMonitor)
    {
        this.inner = inner;
        this.handle = handle;
        this.stderrDrainer = stderrDrainer;
        this.drainCts = drainCts;
        this.exitMonitor = exitMonitor;
    }

    /// <inheritdoc/>
    public string? SessionId => inner.SessionId;

    /// <inheritdoc/>
    public ChannelReader<JsonRpcMessage> MessageReader => inner.MessageReader;

    /// <inheritdoc/>
    public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
        => inner.SendMessageAsync(message, cancellationToken);

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
            await Task.WhenAll(stderrDrainer.PumpTask, exitMonitor).ConfigureAwait(false);
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
