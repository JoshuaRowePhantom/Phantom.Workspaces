using System.IO.Pipes;
using System.Text;

namespace Phantom.Workspaces.Install;

/// <summary>
/// Owns the per-config single-instance mutex plus the activation pipe. The first launch for a
/// given config file becomes the primary (<see cref="IsPrimaryInstance"/>) and listens for
/// activation requests; a later launch for the <em>same</em> config file is secondary, signals
/// the primary to restore from the tray, and exits. Because the identity derives from the config
/// file path (see <see cref="SingleInstanceKey"/>), instances on different config files coexist.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string ActivateMessage = "activate";

    private readonly Mutex mutex;
    private readonly string pipeName;
    private CancellationTokenSource? listenerCancellation;
    private Task? listenerTask;
    private bool disposed;

    private SingleInstanceGuard(Mutex mutex, bool isPrimaryInstance, string pipeName)
    {
        this.mutex = mutex;
        this.IsPrimaryInstance = isPrimaryInstance;
        this.pipeName = pipeName;
    }

    /// <summary>Raised on the primary instance when a secondary launch requests activation.</summary>
    public event EventHandler? ActivationRequested;

    /// <summary>Whether this process is the primary (first) instance for its config file.</summary>
    public bool IsPrimaryInstance { get; }

    /// <summary>
    /// Acquires the single-instance identity for <paramref name="configFilePath"/> (or
    /// <paramref name="explicitInstanceKey"/>). Inspect <see cref="IsPrimaryInstance"/> to decide
    /// whether to run the app (primary) or signal-and-exit (secondary).
    /// </summary>
    public static SingleInstanceGuard Acquire(string? configFilePath, string? explicitInstanceKey = null)
    {
        var key = SingleInstanceKey.Compute(configFilePath, explicitInstanceKey);
        var mutex = new Mutex(initiallyOwned: false, SingleInstanceKey.MutexPrefix + key, out var createdNew);
        return new SingleInstanceGuard(mutex, createdNew, SingleInstanceKey.PipePrefix + key);
    }

    /// <summary>
    /// Starts the background activation listener (primary only). Each connection from a secondary
    /// instance raises <see cref="ActivationRequested"/>.
    /// </summary>
    public void StartActivationListener()
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        if (!this.IsPrimaryInstance || this.listenerTask is not null)
        {
            return;
        }

        this.listenerCancellation = new CancellationTokenSource();
        this.listenerTask = this.ListenLoopAsync(this.listenerCancellation.Token);
    }

    /// <summary>
    /// Signals the primary instance to activate (secondary only). Returns <c>true</c> when the
    /// signal was delivered within <paramref name="timeout"/>.
    /// </summary>
    public async Task<bool> SignalActivationAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var client = new NamedPipeClientStream(
                ".", this.pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            await client.ConnectAsync((int)timeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
            var payload = Encoding.UTF8.GetBytes(ActivateMessage);
            await client.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await client.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    this.pipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                var buffer = new byte[ActivateMessage.Length];
                var read = await server.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                var message = Encoding.UTF8.GetString(buffer, 0, read);
                if (string.Equals(message, ActivateMessage, StringComparison.Ordinal))
                {
                    this.ActivationRequested?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // Broken pipe; loop and accept the next connection.
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.listenerCancellation?.Cancel();
        try
        {
            this.listenerTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Listener teardown races are expected during shutdown.
        }

        this.listenerCancellation?.Dispose();
        this.mutex.Dispose();
    }
}
