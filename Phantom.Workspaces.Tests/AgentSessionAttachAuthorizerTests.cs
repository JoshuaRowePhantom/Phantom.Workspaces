using System.Text.Json;
using Moq;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Services.AgentSessions;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Tests;

public sealed class AgentSessionAttachAuthorizerTests
{
    private const string SessionId = "session";
    private const string OwnerId = "11111111-1111-1111-1111-111111111111";
    private const string NewOwnerId = "22222222-2222-2222-2222-222222222222";
    private const string UserId = "33333333-3333-3333-3333-333333333333";

    [Fact]
    public async Task AuthorizeAsync_OwnerPeerAllowed_ReturnsAllow()
    {
        var layer = Layer(Result(UserId, UserId));
        var decision = await new AgentSessionAttachAuthorizer(layer.Object).AuthorizeAsync(
            Peer(UserId), Request(), TestContext.Current.CancellationToken);
        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public async Task AuthorizeAsync_UnrelatedPeerDenied_ReturnsIndistinguishableDenial()
    {
        var layer = Layer(Result(UserId, UserId));
        var decision = await new AgentSessionAttachAuthorizer(layer.Object).AuthorizeAsync(
            Peer(Guid.NewGuid().ToString()), Request(), TestContext.Current.CancellationToken);
        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public async Task AuthorizeAsync_MutationAfterAclChange_DeniesPreviouslyAttachedPeer()
    {
        var layer = new Mock<IDataAccessLayer>();
        layer.SetupSequence(value => value.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result(UserId, UserId))
            .ReturnsAsync(Result(Guid.NewGuid().ToString(), UserId));
        var authorizer = new AgentSessionAttachAuthorizer(layer.Object);
        Assert.True((await authorizer.AuthorizeAsync(
            Peer(UserId), Request(), TestContext.Current.CancellationToken)).IsAllowed);
        Assert.False((await authorizer.AuthorizeAsync(
            Peer(UserId), Request(AgentSessionAuthorizationOperation.Send),
            TestContext.Current.CancellationToken)).IsAllowed);
    }

    [Fact]
    public async Task AuthorizeAsync_TakeoverWithoutOwnerPermission_Denies()
    {
        var layer = Layer(Result(UserId, Guid.NewGuid().ToString()));
        var decision = await new AgentSessionAttachAuthorizer(layer.Object).AuthorizeAsync(
            Peer(UserId),
            Request(AgentSessionAuthorizationOperation.Takeover) with
            {
                NewOwningProfileEntityId = NewOwnerId,
            },
            TestContext.Current.CancellationToken);
        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public async Task AuthorizeAsync_Cancelled_PerformsNoRuntimeLookupOrMutation()
    {
        var layer = new Mock<IDataAccessLayer>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AgentSessionAttachAuthorizer(layer.Object).AuthorizeAsync(
                Peer(UserId), Request(), cancellation.Token).AsTask());
        layer.Verify(value => value.QueryAsync(
            It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static AgentSessionAuthorizationRequest Request(
        AgentSessionAuthorizationOperation operation = AgentSessionAuthorizationOperation.Open) => new()
    {
        AgentSessionId = SessionId,
        ExpectedOwningProfileEntityId = OwnerId,
        ExpectedOwnershipGeneration = 1,
        Operation = operation,
    };

    private static TransportPeerIdentity Peer(string userId) => new()
    {
        AuthenticationScheme = "test",
        StablePeerId = "peer",
        UserEntityId = userId,
    };

    private static Mock<IDataAccessLayer> Layer(QueryResult result)
    {
        var layer = new Mock<IDataAccessLayer>();
        layer.Setup(value => value.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return layer;
    }

    private static QueryResult Result(string ownerUserId, string newOwnerUserId)
        => new()
        {
            Batches =
            [
                new TimestampedQueryBatch
                {
                    Timestamp = new Timestamp(DateTimeOffset.UnixEpoch, "change"),
                    Entities =
                    [
                        Entity(Guid.NewGuid().ToString(), $$"""
                            {"agent-session-id":"{{SessionId}}","owning-profile-entity-id":"{{OwnerId}}","ownership-generation":1}
                            """),
                        Entity(OwnerId, $$"""{"user-entity-id":"{{ownerUserId}}"}"""),
                        Entity(NewOwnerId, $$"""{"user-entity-id":"{{newOwnerUserId}}"}"""),
                    ],
                },
            ],
        };

    private static QueryEntitySnapshot Entity(string id, string json) => new()
    {
        EntityId = new EntityId(id),
        ConcurrencyTag = new ConcurrencyTag("revision"),
        ModifiedTime = new Timestamp(DateTimeOffset.UnixEpoch, "change"),
        Data = JsonDocument.Parse(json).RootElement.Clone(),
        Relationships = [],
        MatchingClauseIdentifiers = [],
    };
}
