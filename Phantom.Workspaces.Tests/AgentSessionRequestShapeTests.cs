using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Services.AgentSessions;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Tests;

public sealed class AgentSessionRequestShapeTests
{
    [Fact]
    public void TransportPeerIdentity_BlankAuthenticatedIdentity_RejectsInitialization()
        => Assert.Throws<ArgumentException>(() => new TransportPeerIdentity
        {
            AuthenticationScheme = " ",
            StablePeerId = "peer",
        });

    [Fact]
    public void RuntimeRequestTypes_RequiredMembersDefaultsAndNamedInitializers_MapToLifecycleOperations()
    {
        var epoch = new RuntimeEpoch { Value = Guid.NewGuid() };
        var terminate = new TerminateAgentSessionRuntimeRequest
        {
            SessionId = "session", OwnershipGeneration = 4, Epoch = epoch,
        };
        var retention = new UpdateAgentSessionRuntimeRetentionRequest
        {
            SessionId = "session", OwnershipGeneration = 4, Epoch = epoch, ContinueInBackground = true,
        };
        Assert.Equal(terminate.Epoch, retention.Epoch);
        Assert.True(retention.ContinueInBackground);
    }

    [Fact]
    public void AgentSessionAuthorizationRequest_RequiredMembersDefaultsAndNamedInitializer_MapToAuthorization()
    {
        var request = new AgentSessionAuthorizationRequest
        {
            AgentSessionId = "session",
            ExpectedOwningProfileEntityId = Guid.NewGuid().ToString(),
            ExpectedOwnershipGeneration = 2,
            Operation = AgentSessionAuthorizationOperation.Open,
        };
        Assert.Null(request.ChildAgentId);
        Assert.Equal(AgentSessionAuthorizationOperation.Open, request.Operation);
    }
}
