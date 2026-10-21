namespace Phantom.Workspaces.Services.DevTunnel;

internal static class DevTunnelOwnedResource
{
    internal static async ValueTask DisposeAsync(object? resource)
    {
        if (resource is IAsyncDisposable asynchronous)
        {
            await asynchronous.DisposeAsync().ConfigureAwait(false);
        }
        else if (resource is IDisposable synchronous)
        {
            synchronous.Dispose();
        }
    }
}
