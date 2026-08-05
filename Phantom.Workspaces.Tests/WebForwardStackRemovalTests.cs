using System.Linq;
using Phantom.Workspaces.Trust;
using Xunit;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// Guards the completion of the unified-transport cutover (#1044): the production-orphaned
/// web-forward "remote" stack must remain fully removed from the <c>Phantom.Workspaces</c>
/// application assembly. Remote execution now flows exclusively through the transport surfaces
/// (<c>TransportTrustedExecutor</c> behind <c>DeferredTrustedExecutorSelector</c>).
/// </summary>
public sealed class WebForwardStackRemovalTests
{
    private static readonly string[] RemovedTypeNames =
    [
        "WebRemoteChatClient",
        "RemoteAgentChatClient",
        "RemoteTrustedExecutor",
        "WebRemoteStreamClient",
        "RemoteExecutionRegistry",
        "DynamicRemoteTrustedExecutor",
    ];

    [Fact]
    public void Production_NoWebForwardRemoteStack_Remains()
    {
        // DeferredTrustedExecutorSelector anchors the Phantom.Workspaces application assembly.
        var assembly = typeof(DeferredTrustedExecutorSelector).Assembly;

        var stillPresent = assembly.GetTypes()
            .Where(type => RemovedTypeNames.Contains(type.Name))
            .Select(type => type.FullName)
            .ToArray();

        Assert.Empty(stillPresent);
    }

    [Fact]
    public void Production_RetainsDeferredTrustedExecutorSelector()
    {
        var assembly = typeof(DeferredTrustedExecutorSelector).Assembly;

        Assert.NotNull(
            assembly.GetType("Phantom.Workspaces.Trust.DeferredTrustedExecutorSelector"));
    }
}
