using System.Linq;
using Phantom.Workspaces.Llm.Core.Transport;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Web.Server;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Guards the unified-transport production cutover: the old reverse-execution /
/// <c>ReverseExecutionRegistry</c> / <c>CreateSelector</c> stack must remain fully removed from the
/// production Llm.Core assembly. Remote and reverse execution now flow exclusively through the
/// transport surfaces (transport factory registry + <c>/reverse-transport/connect</c> relay).
/// </summary>
public sealed class ArchitectureRegressionTests
{
    private static readonly string[] RemovedTypeNames =
    [
        "ReverseExecutionRegistry",
        "TrustedExecutorComposition",
        "ReverseTrustedExecutor",
        "ReverseRemoteChatClient",
        "ReverseConnectionAcceptor",
        "ReverseExecutionClientHost",
        "LocalReverseExecutionHandler",
        "IReverseMessageChannel",
        "ReverseFrame",
        "ReverseChannelConnection",
        "ReverseExecutionWorker",
        "HttpReverseMessageChannel",
        "WebSocketReverseMessageChannel",
        "IReverseExecutionHandler",
    ];

    [Fact]
    public void Production_NoReferenceTo_ReverseExecutionRegistry_Remains()
    {
        // The production assembly that formerly hosted the old reverse-execution stack.
        var assembly = typeof(LocalTrustedExecutor).Assembly;

        var stillPresent = assembly.GetTypes()
            .Where(type => RemovedTypeNames.Contains(type.Name))
            .Select(type => type.FullName)
            .ToArray();

        Assert.Empty(stillPresent);
    }

    [Fact]
    public void Production_RetainsLocalTrustedExecutorAndSelectorContracts()
    {
        var assembly = typeof(LocalTrustedExecutor).Assembly;

        Assert.NotNull(assembly.GetType("Phantom.Workspaces.Llm.Trust.ITrustedExecutor"));
        Assert.NotNull(assembly.GetType("Phantom.Workspaces.Llm.Trust.ITrustedExecutorSelector"));
        Assert.NotNull(assembly.GetType("Phantom.Workspaces.Llm.Trust.LocalTrustedExecutor"));
    }

    [Fact]
    public void Production_RetainsTransportTrustedExecutor()
    {
        // The transport-backed remote executor is the production remote path after the cutover.
        Assert.NotNull(
            typeof(TransportTrustedExecutor).Assembly.GetType(
                "Phantom.Workspaces.Llm.Core.Transport.TransportTrustedExecutor"));
    }

    [Fact]
    public void WebServer_NoLongerHosts_StreamEndpoints()
    {
        // The web-forward stream endpoint (server side of the deleted WebRemoteStreamClient) is gone.
        var webServerAssembly = typeof(AgentRespondHandler).Assembly;

        var stillPresent = webServerAssembly.GetTypes()
            .Where(type => type.Name == "StreamEndpointRouteBuilderExtensions")
            .Select(type => type.FullName)
            .ToArray();

        Assert.Empty(stillPresent);
    }
}
