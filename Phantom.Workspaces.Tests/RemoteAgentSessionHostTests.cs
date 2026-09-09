using Moq;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Services.AgentSessions;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Tests;

public sealed partial class RemoteAgentSessionHostTests
{
    [Fact]
    public async Task GetStatusAsync_UnauthorizedPeer_ReturnsUnavailableWithoutRuntimeLookup()
    {
        var authorizer = new Mock<IAgentSessionAttachAuthorizer>();
        authorizer.Setup(value => value.AuthorizeAsync(
                It.IsAny<TransportPeerIdentity>(), It.IsAny<AgentSessionAuthorizationRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentSessionAuthorizationDecision { IsAllowed = false });
        var registry = new Mock<IRemoteAgentSessionRuntimeRegistry>();
        var host = new RemoteAgentSessionHost(authorizer.Object, registry.Object, Mock.Of<IAgentSessionRuntimeHostFactory>());

        var status = await host.GetStatusAsync(Peer(), Open(AgentSessionOpenIntent.Status), TestContext.Current.CancellationToken);

        Assert.Equal(AgentSessionRemoteStatus.Unavailable, status);
        registry.Verify(value => value.TryGetAsync(
            It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OpenAsync_AttachMissingRuntime_ReturnsIndistinguishableNotFound()
    {
        var authorizer = new Mock<IAgentSessionAttachAuthorizer>();
        authorizer.Setup(value => value.AuthorizeAsync(
                It.IsAny<TransportPeerIdentity>(), It.IsAny<AgentSessionAuthorizationRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentSessionAuthorizationDecision { IsAllowed = true });
        var registry = new Mock<IRemoteAgentSessionRuntimeRegistry>();
        registry.Setup(value => value.TryGetAsync("session", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RemoteAgentSessionLease?)null);
        var host = new RemoteAgentSessionHost(authorizer.Object, registry.Object, Mock.Of<IAgentSessionRuntimeHostFactory>());

        await Assert.ThrowsAsync<AgentSessionUnavailableException>(() => host.OpenAsync(
            new OpenAgentSessionHostRequest
            {
                Peer = Peer(),
                OpenRequest = Open(AgentSessionOpenIntent.Attach),
                Channel = Mock.Of<IMessageChannel>(),
            },
            TestContext.Current.CancellationToken));
    }

    private static TransportPeerIdentity Peer() => new()
    {
        AuthenticationScheme = "test",
        StablePeerId = "peer",
        UserEntityId = "11111111-1111-1111-1111-111111111111",
    };

    private static AgentSessionOpenRequest Open(AgentSessionOpenIntent intent) => new()
    {
        ProtocolVersion = 1,
        AgentSessionId = "session",
        ExpectedOwningProfileEntityId = "22222222-2222-2222-2222-222222222222",
        ExpectedOwnershipGeneration = 1,
        OpenIntent = intent,
        AttachmentToken = Guid.NewGuid().ToString("N"),
        Capabilities = [],
    };
}
