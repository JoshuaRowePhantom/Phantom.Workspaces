using System.Threading.Tasks;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Transport.ReverseHttp;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class WorkspacesWebHostTests
{
    [Fact]
    public async Task Constructor_ExposesTransportConnectionStatusRegistry()
    {
        var statusRegistry = new ReverseConnectionStatusRegistry();

        await using var host = new WorkspacesWebHost(statusRegistry);

        // The host now sources its reverse hub from the transport connection-status registry rather
        // than a ReverseExecutionRegistry, and exposes the same instance it maps into the server.
        Assert.Same(statusRegistry, host.ConnectionStatusRegistry);
        Assert.False(host.IsRunning);
        Assert.Null(host.ListenUrl);
    }
}
