using System.Text.Json;
using System.Threading.Channels;

namespace Phantom.Workspaces.Transport.ReverseHttp;

internal sealed class RelaySession : IAsyncDisposable
{
    private readonly IMessageChannel first;
    private readonly IMessageChannel second;
    private readonly CancellationTokenSource shutdown;
    private readonly Task firstToSecond;
    private readonly Task secondToFirst;
    private int shutdownStarted;

    public RelaySession(IMessageChannel first, IMessageChannel second, CancellationToken cancellationToken = default)
    {
        this.first = first;
        this.second = second;
        this.shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        this.firstToSecond = this.PumpAsync(first, second);
        this.secondToFirst = this.PumpAsync(second, first);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.shutdownStarted, 1) == 0)
        {
            await this.WriteCloseAsync(this.first).ConfigureAwait(false);
            await this.WriteCloseAsync(this.second).ConfigureAwait(false);
        }

        this.shutdown.Cancel();
        await this.WhenPumpsCompleteAsync().ConfigureAwait(false);
        await this.DisposeChannelsAsync().ConfigureAwait(false);
        this.shutdown.Dispose();
    }

    private async Task PumpAsync(IMessageChannel source, IMessageChannel destination)
    {
        try
        {
            await foreach (var frame in source.Reader.ReadAllAsync(this.shutdown.Token).ConfigureAwait(false))
            {
                await destination.Writer.WriteAsync(frame.Clone(), this.shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (this.shutdown.IsCancellationRequested)
        {
        }
        catch (ChannelClosedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            await this.ShutdownFromClosedSideAsync(destination).ConfigureAwait(false);
        }
    }

    private async ValueTask ShutdownFromClosedSideAsync(IMessageChannel stillConnected)
    {
        if (Interlocked.Exchange(ref this.shutdownStarted, 1) != 0)
        {
            return;
        }

        await this.WriteCloseAsync(stillConnected).ConfigureAwait(false);
        this.shutdown.Cancel();
        await this.DisposeChannelsAsync().ConfigureAwait(false);
    }

    private async ValueTask WhenPumpsCompleteAsync()
    {
        try
        {
            await Task.WhenAll(this.firstToSecond, this.secondToFirst).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (this.shutdown.IsCancellationRequested)
        {
        }
    }

    private async ValueTask DisposeChannelsAsync()
    {
        await this.first.DisposeAsync().ConfigureAwait(false);
        await this.second.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask WriteCloseAsync(IMessageChannel channel)
    {
        try
        {
            using var document = JsonDocument.Parse("""{"type":"channel-close"}""");
            await channel.Writer.WriteAsync(document.RootElement.Clone(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }
}
