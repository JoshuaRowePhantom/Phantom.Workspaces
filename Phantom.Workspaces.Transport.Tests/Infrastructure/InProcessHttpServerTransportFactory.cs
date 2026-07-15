using System.Collections.Concurrent;
using Phantom.Workspaces.Transport.Http;

namespace Phantom.Workspaces.Transport.Tests.Infrastructure;

public sealed class InProcessHttpServerTransportFactory : IAsyncDisposable
{
    private readonly ConcurrentDictionary<ITransport, DateTimeOffset> activeTransports = new();
    private readonly TimeSpan leaseDuration;
    private readonly Func<DateTimeOffset> utcNow;

    public InProcessHttpServerTransportFactory(TimeSpan? leaseDuration = null, Func<DateTimeOffset>? utcNow = null)
    {
        this.Registry = new TransportRegistry();
        this.leaseDuration = leaseDuration ?? new TransportOptions().ServerLeaseDuration;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public TransportRegistry Registry { get; }

    public int ActiveTransportCount => this.activeTransports.Count;

    public Task AcceptAsync(ITransport clientTransport, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(clientTransport);
        ct.ThrowIfCancellationRequested();
        this.activeTransports[clientTransport] = this.utcNow();
        return Task.CompletedTask;
    }

    public async Task SweepExpiredLeasesAsync(CancellationToken ct = default)
    {
        var now = this.utcNow();
        foreach (var (transport, acceptedAt) in this.activeTransports.ToArray())
        {
            ct.ThrowIfCancellationRequested();
            if (now - acceptedAt >= this.leaseDuration && this.activeTransports.TryRemove(transport, out _))
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var transport in this.activeTransports.Keys)
        {
            if (this.activeTransports.TryRemove(transport, out _))
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}