using System.Text.Json;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Transport;

internal sealed class ReachabilityRouteLease : IAsyncDisposable
{
    private readonly IReachabilityRouteStore routeStore;
    private readonly EntityId profileEntityId;
    private readonly string routeId;
    private readonly Func<JsonElement> descriptorProvider;
    private readonly int priority;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan leaseDuration;
    private readonly Action<Exception?> publicationStatusChanged;
    private readonly CancellationTokenSource shutdown = new();
    private Task? renewalLoop;
    private int disposed;

    public ReachabilityRouteLease(
        IReachabilityRouteStore routeStore,
        EntityId profileEntityId,
        string routeId,
        Func<JsonElement> descriptorProvider,
        int priority,
        TimeProvider timeProvider,
        TimeSpan leaseDuration,
        Action<Exception?> publicationStatusChanged)
    {
        this.routeStore = routeStore ?? throw new ArgumentNullException(nameof(routeStore));
        this.profileEntityId = profileEntityId;
        this.routeId = routeId;
        this.descriptorProvider = descriptorProvider ?? throw new ArgumentNullException(nameof(descriptorProvider));
        this.priority = priority;
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.leaseDuration = leaseDuration > TimeSpan.Zero
            ? leaseDuration
            : throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        this.publicationStatusChanged = publicationStatusChanged ?? throw new ArgumentNullException(nameof(publicationStatusChanged));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await this.PublishAsync(cancellationToken).ConfigureAwait(false);
        this.renewalLoop ??= this.RunRenewalLoopAsync();
    }

    public async Task PublishAsync(CancellationToken cancellationToken)
    {
        var now = this.timeProvider.GetUtcNow();
        var route = new ReachabilityRoute
        {
            RouteId = this.routeId,
            Descriptor = this.descriptorProvider().Clone(),
            OwnerProfileEntityId = this.profileEntityId,
            Priority = this.priority,
            LastConfirmed = now,
            ExpiresAt = now.Add(this.leaseDuration),
        };

        try
        {
            await this.routeStore.UpsertRouteAsync(
                this.profileEntityId,
                route,
                cancellationToken).ConfigureAwait(false);
            this.publicationStatusChanged(null);
        }
        catch (ReachabilityRouteStoreException exception)
        {
            this.publicationStatusChanged(exception);
        }
        catch (InvalidOperationException exception)
        {
            this.publicationStatusChanged(exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
        {
            return;
        }

        await this.shutdown.CancelAsync().ConfigureAwait(false);
        if (this.renewalLoop is not null)
        {
            await this.renewalLoop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        try
        {
            await this.routeStore.RemoveRouteAsync(
                this.profileEntityId,
                this.routeId,
                this.profileEntityId,
                CancellationToken.None).ConfigureAwait(false);
            this.publicationStatusChanged(null);
        }
        catch (ReachabilityRouteStoreException exception)
        {
            this.publicationStatusChanged(exception);
        }
        catch (InvalidOperationException exception)
        {
            this.publicationStatusChanged(exception);
        }

        this.shutdown.Dispose();
    }

    private async Task RunRenewalLoopAsync()
    {
        using var timer = new PeriodicTimer(this.leaseDuration / 2, this.timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(this.shutdown.Token).ConfigureAwait(false))
            {
                await this.PublishAsync(this.shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (this.shutdown.IsCancellationRequested)
        {
        }
    }
}
