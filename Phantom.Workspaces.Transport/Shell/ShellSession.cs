using System.Diagnostics;

namespace Phantom.Workspaces.Transport.Shell;

public sealed class ShellSession : IAsyncDisposable
{
    private readonly Process process;
    private readonly Stream transportStream;
    private readonly CancellationTokenSource cts;
    private readonly Task relayInputTask;
    private readonly Task relayOutputTask;
    private readonly Task relayErrorTask;
    private readonly Task exitWatcher;
    private int disposed;

    internal ShellSession(Process process, Stream transportStream, CancellationToken cancellationToken)
    {
        this.process = process ?? throw new ArgumentNullException(nameof(process));
        this.transportStream = transportStream ?? throw new ArgumentNullException(nameof(transportStream));
        this.cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        this.relayInputTask = Task.Run(() => this.RelayInputAsync(this.cts.Token), CancellationToken.None);
        this.relayOutputTask = Task.Run(() => this.RelayOutputAsync(process.StandardOutput.BaseStream, this.cts.Token), CancellationToken.None);
        this.relayErrorTask = Task.Run(() => this.RelayOutputAsync(process.StandardError.BaseStream, this.cts.Token), CancellationToken.None);
        this.exitWatcher = Task.Run(this.WatchExitAsync, CancellationToken.None);
    }

    public int ProcessId => this.process.Id;

    public bool HasExited => this.process.HasExited;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
        {
            return;
        }

        await this.cts.CancelAsync().ConfigureAwait(false);
        try
        {
            if (!this.process.HasExited)
            {
                this.process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }

        await SuppressAsync(this.relayInputTask).ConfigureAwait(false);
        await SuppressAsync(this.relayOutputTask).ConfigureAwait(false);
        await SuppressAsync(this.relayErrorTask).ConfigureAwait(false);
        await SuppressAsync(this.exitWatcher).ConfigureAwait(false);
        await this.transportStream.DisposeAsync().ConfigureAwait(false);
        this.process.Dispose();
        this.cts.Dispose();
    }

    private async Task WatchExitAsync()
    {
        try
        {
            await Task.WhenAll(this.relayOutputTask, this.relayErrorTask).ConfigureAwait(false);
        }
        catch
        {
        }

        if (this.disposed == 0)
        {
            try
            {
                await this.transportStream.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private async Task RelayInputAsync(CancellationToken ct)
    {
        try
        {
            await this.transportStream.CopyToAsync(this.process.StandardInput.BaseStream, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            try
            {
                this.process.StandardInput.Close();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private async Task RelayOutputAsync(Stream source, CancellationToken ct)
    {
        try
        {
            await source.CopyToAsync(this.transportStream, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static async Task SuppressAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }
}
