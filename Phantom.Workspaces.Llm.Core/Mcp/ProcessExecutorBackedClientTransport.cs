using System.Text;
using System.Threading.Channels;
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
/// separately from the protocol stream and forwarded to the logger and a bounded rolling
/// diagnostic buffer. A nonzero premature exit faults the transport and disposal always kills the
/// process tree.
/// </summary>
public sealed class ProcessExecutorBackedClientTransport : IClientTransport
{
    private const int StderrRollingCapacity = 8 * 1024;

    private readonly ProcessExecutionRequest request;
    private readonly IProcessExecutor executor;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger logger;

    private int connectCount;

    /// <summary>Construct a transport for one MCP stdio server described by <paramref name="request"/>.</summary>
    public ProcessExecutorBackedClientTransport(
        string name,
        ProcessExecutionRequest request,
        IProcessExecutor executor,
        ILoggerFactory? loggerFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(executor);

        Name = name;
        this.request = request;
        this.executor = executor;
        this.loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        this.logger = this.loggerFactory.CreateLogger<ProcessExecutorBackedClientTransport>();
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref connectCount) != 1)
        {
            throw new InvalidOperationException(
                $"ProcessExecutorBackedClientTransport '{Name}' has already been connected.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        IProcessHandle? handle = null;
        StderrDrainer? stderrDrainer = null;
        CancellationTokenSource? drainCts = null;
        try
        {
            handle = executor.Start(request);
            drainCts = new CancellationTokenSource();
            stderrDrainer = new StderrDrainer(handle.StandardError, logger, Name, drainCts.Token);
            var exitTask = handle.WaitAsync(drainCts.Token);

            var streamTransport = new StreamClientTransport(
                handle.StandardInput,
                handle.StandardOutput,
                loggerFactory);
            var inner = await streamTransport.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return new ProcessOwnedMcpTransport(
                inner,
                handle,
                stderrDrainer,
                drainCts,
                exitTask,
                Name);
        }
        catch
        {
            try
            {
                if (drainCts is not null)
                {
                    await drainCts.CancelAsync().ConfigureAwait(false);
                    drainCts.Dispose();
                }
            }
            catch
            {
            }

            if (handle is not null)
            {
                try
                {
                    await handle.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                }
            }
            throw;
        }
    }

    /// <summary>Drains child stderr into the logger and a bounded rolling diagnostic buffer.</summary>
    internal sealed class StderrDrainer
    {
        private readonly Stream source;
        private readonly ILogger logger;
        private readonly string name;
        private readonly Channel<string> lines = Channel.CreateBounded<string>(
            new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
        private readonly Task pumpTask;
        private readonly StringBuilder rolling = new(StderrRollingCapacity);
        private readonly object rollingLock = new();

        public StderrDrainer(Stream source, ILogger logger, string name, CancellationToken cancellationToken)
        {
            this.source = source;
            this.logger = logger;
            this.name = name;
            pumpTask = Task.Run(() => PumpAsync(cancellationToken), CancellationToken.None);
        }

        public Task PumpTask => pumpTask;

        public string SnapshotRolling()
        {
            lock (rollingLock)
            {
                return rolling.ToString();
            }
        }

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
                    logger.LogInformation("MCP stdio server '{Name}' stderr: {Line}", name, line);
                    AppendRolling(line);
                    lines.Writer.TryWrite(line);
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
                lines.Writer.TryComplete();
            }
        }

        private void AppendRolling(string line)
        {
            var sanitized = new string(
                line.Select(static character => char.IsControl(character) && character != '\t' ? ' ' : character)
                    .ToArray());
            if (sanitized.Length >= StderrRollingCapacity)
                sanitized = sanitized[^StderrRollingCapacity..];

            lock (rollingLock)
            {
                var separatorLength = rolling.Length > 0 ? 1 : 0;
                var overflow = rolling.Length + separatorLength + sanitized.Length - StderrRollingCapacity;
                if (overflow > 0)
                {
                    rolling.Remove(0, Math.Min(rolling.Length, overflow));
                }
                if (rolling.Length > 0)
                    rolling.Append('\n');
                rolling.Append(sanitized);

                if (rolling.Length > StderrRollingCapacity)
                    rolling.Remove(0, rolling.Length - StderrRollingCapacity);
            }
        }
    }
}
