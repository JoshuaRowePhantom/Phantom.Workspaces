using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Phantom.Workspaces.Llm.Processes;

namespace Phantom.Workspaces.Llm.Mcp;

/// <summary>
/// MCP <see cref="IClientTransport"/> that launches its child process through the policy-aware
/// #1474 <see cref="IProcessExecutor"/> and speaks MCP JSON-RPC over the child's stdin/stdout via
/// the SDK's <c>StreamClientTransport</c> (issue #1477). A null <see cref="ProcessExecutionRequest.MxcPolicy"/>
/// selects the ordinary-process branch; a compiled policy selects MXC. Stderr is drained
/// separately from the protocol stream. Only counts are logged, never stderr content.
/// A nonzero premature exit faults the transport and disposal always kills the
/// process tree.
/// </summary>
public sealed class ProcessExecutorBackedClientTransport : IClientTransport, IAsyncDisposable
{
    private readonly ProcessExecutionRequest request;
    private readonly IProcessExecutor executor;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger logger;
    private readonly Func<Stream, Stream, ILoggerFactory, CancellationToken, Task<ITransport>>
        connectStreamTransport;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly object lifecycleGate = new();

    private int disposed;
    private Task<ITransport>? connectionTask;
    private ITransport? connectedTransport;

    /// <summary>Construct a transport for one MCP stdio server described by <paramref name="request"/>.</summary>
    public ProcessExecutorBackedClientTransport(
        string name,
        ProcessExecutionRequest request,
        IProcessExecutor executor,
        ILoggerFactory? loggerFactory)
        : this(name, request, executor, loggerFactory, ConnectStreamTransportAsync)
    {
    }

    internal ProcessExecutorBackedClientTransport(
        string name,
        ProcessExecutionRequest request,
        IProcessExecutor executor,
        ILoggerFactory? loggerFactory,
        Func<Stream, Stream, ILoggerFactory, CancellationToken, Task<ITransport>> connectStreamTransport)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(executor);

        Name = name;
        this.request = request;
        this.executor = executor;
        this.loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        this.logger = this.loggerFactory.CreateLogger<ProcessExecutorBackedClientTransport>();
        this.connectStreamTransport = connectStreamTransport
                                      ?? throw new ArgumentNullException(nameof(connectStreamTransport));
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
    {
        lock (lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (connectionTask is not null)
            {
                throw new InvalidOperationException(
                    "The MCP process transport has already been connected.");
            }

            connectionTask = ConnectCoreAsync(cancellationToken);
            return connectionTask;
        }
    }

    private async Task<ITransport> ConnectCoreAsync(CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetimeCancellation.Token);
        var lifetimeToken = linkedCancellation.Token;
        lifetimeToken.ThrowIfCancellationRequested();

        IProcessHandle? handle = null;
        StderrDrainer? stderrDrainer = null;
        CancellationTokenSource? drainCts = null;
        ITransport? inner = null;
        ProcessOwnedMcpTransport? ownedTransport = null;
        Task<ProcessExitResult>? exitTask = null;
        var ownershipTransferred = false;
        try
        {
            handle = await executor.StartAsync(request, lifetimeToken).ConfigureAwait(false);
            drainCts = new CancellationTokenSource();
            stderrDrainer = new StderrDrainer(handle.StandardError, logger, Name, drainCts.Token);
            exitTask = handle.WaitAsync(drainCts.Token);

            inner = await connectStreamTransport(
                handle.StandardInput,
                handle.StandardOutput,
                loggerFactory,
                lifetimeToken).ConfigureAwait(false);
            ownedTransport = new ProcessOwnedMcpTransport(
                inner,
                handle,
                stderrDrainer,
                drainCts,
                exitTask,
                Name,
                logger);

            lock (lifecycleGate)
            {
                ObjectDisposedException.ThrowIf(disposed != 0, this);
                connectedTransport = ownedTransport;
                ownershipTransferred = true;
            }

            return ownedTransport;
        }
        finally
        {
            if (!ownershipTransferred)
            {
                if (ownedTransport is not null)
                {
                    await ObserveCleanupAsync(
                        () => DisposeAsyncTask(ownedTransport),
                        "disposing the partially connected MCP transport").ConfigureAwait(false);
                }
                else
                {
                    if (inner is not null)
                    {
                        await ObserveCleanupAsync(
                            () => DisposeAsyncTask(inner),
                            "disposing the MCP SDK transport after startup failed").ConfigureAwait(false);
                    }

                    if (handle is not null)
                    {
                        await ObserveCleanupAsync(
                            () => DisposeAsyncTask(handle),
                            "disposing the MCP process tree after startup failed").ConfigureAwait(false);
                    }

                    if (drainCts is not null)
                    {
                        await ObserveCleanupAsync(
                            () => CancelAsyncTask(drainCts),
                            "cancelling MCP pipe drains after startup failed").ConfigureAwait(false);
                    }

                    if (stderrDrainer is not null)
                    {
                        await ObserveCleanupAsync(
                            () => stderrDrainer.PumpTask,
                            "joining the MCP stderr drainer after startup failed").ConfigureAwait(false);
                    }
                    if (exitTask is not null)
                    {
                        await ObserveCleanupAsync(
                            () => exitTask,
                            "joining MCP process exit after startup failed",
                            cancellationIsExpected: true).ConfigureAwait(false);
                    }

                    if (drainCts is not null)
                    {
                        await ObserveCleanupAsync(
                            () => DisposeCancellationSourceTask(drainCts),
                            "disposing MCP pipe-drain cancellation after startup failed").ConfigureAwait(false);
                    }
                }
            }
        }
    }

    /// <summary>Disposes a connected process transport, including initialization still in progress.</summary>
    public async ValueTask DisposeAsync()
    {
        Task<ITransport>? pendingConnection;
        ITransport? transport;
        lock (lifecycleGate)
        {
            if (disposed != 0)
            {
                return;
            }

            disposed = 1;
            pendingConnection = connectionTask;
            transport = connectedTransport;
            connectedTransport = null;
        }

        await lifetimeCancellation.CancelAsync().ConfigureAwait(false);
        if (pendingConnection is not null)
        {
            await ObserveAsync(pendingConnection).ConfigureAwait(false);
            if (pendingConnection.IsCompletedSuccessfully)
            {
                transport ??= await pendingConnection.ConfigureAwait(false);
            }
        }

        if (transport is not null)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }

        lifetimeCancellation.Dispose();
    }

    private static async Task ObserveAsync(Task task)
    {
        await Task.WhenAny(task).ConfigureAwait(false);
        _ = task.Exception;
    }

    private async Task ObserveCleanupAsync(
        Func<Task> cleanup,
        string operation,
        bool cancellationIsExpected = false)
    {
        try
        {
            await cleanup().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationIsExpected)
        {
        }
        catch (Exception)
        {
            logger.LogWarning(
                "Secondary cleanup failure during MCP stdio operation {Operation}; category failure.",
                operation);
        }
    }

    private static async Task DisposeAsyncTask(IAsyncDisposable disposable) =>
        await disposable.DisposeAsync().ConfigureAwait(false);

    private static async Task CancelAsyncTask(CancellationTokenSource cancellation) =>
        await cancellation.CancelAsync().ConfigureAwait(false);

    private static Task DisposeCancellationSourceTask(CancellationTokenSource cancellation)
    {
        cancellation.Dispose();
        return Task.CompletedTask;
    }

    private static Task<ITransport> ConnectStreamTransportAsync(
        Stream input,
        Stream output,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var streamTransport = new StreamClientTransport(input, output, loggerFactory);
        return streamTransport.ConnectAsync(cancellationToken);
    }

    /// <summary>Drains child stderr without retaining or logging untrusted text.</summary>
    internal sealed class StderrDrainer
    {
        private readonly Stream source;
        private readonly ILogger logger;
        private readonly Task pumpTask;
        private int lineCount;

        public StderrDrainer(Stream source, ILogger logger, string name, CancellationToken cancellationToken)
        {
            this.source = source;
            this.logger = logger;
            // Enter the first asynchronous read before startup can fail and begin cleanup.
            pumpTask = PumpAsync(cancellationToken);
        }

        public Task PumpTask => pumpTask;

        public int LineCount => Volatile.Read(ref this.lineCount);

        private async Task PumpAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Strip a UTF-8 BOM if the child emits one on its first stderr write.
                using var reader = new StreamReader(
                    source,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 4096,
                    leaveOpen: true);
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null)
                        break;

                    // Never route stderr through the JSON-RPC message channel.
                    Interlocked.Increment(ref this.lineCount);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                logger.LogDebug("MCP stdio stderr drained; lines {LineCount}; outcome terminal.",
                    this.LineCount);
            }
        }
    }
}
