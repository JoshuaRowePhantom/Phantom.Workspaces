using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using AgentSchema;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class AgentSessionProtocolCodecTests
{
    [Fact]
    public void RuntimeEpoch_EmptyValue_RejectsInitialization()
        => Assert.Throws<ArgumentException>(() => new RuntimeEpoch { Value = Guid.Empty });

    [Fact]
    public void ReplayCursor_NegativeSequence_RejectsInitialization()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new ReplayCursor
        {
            Epoch = Epoch(), Sequence = -1,
        });

    [Fact]
    public void TransportPeerIdentity_BlankAuthenticatedIdentity_RejectsInitialization()
    {
        Assert.Throws<ArgumentException>(() => new TransportPeerIdentity
        {
            AuthenticationScheme = " ",
            StablePeerId = "peer",
        });
        Assert.Throws<ArgumentException>(() => new TransportPeerIdentity
        {
            AuthenticationScheme = "mxc",
            StablePeerId = "",
        });
    }

    [Fact]
    public void ProtocolDtos_RequiredInitProperties_AreMarkedRequired()
    {
        AssertRequired<AgentSessionOpenRequest>(
            nameof(AgentSessionOpenRequest.ProtocolVersion),
            nameof(AgentSessionOpenRequest.AgentSessionId),
            nameof(AgentSessionOpenRequest.ExpectedOwningProfileEntityId),
            nameof(AgentSessionOpenRequest.ExpectedOwnershipGeneration),
            nameof(AgentSessionOpenRequest.OpenIntent),
            nameof(AgentSessionOpenRequest.AttachmentToken),
            nameof(AgentSessionOpenRequest.Capabilities));
        AssertRequired<RemoteSubagentDescriptor>(
            nameof(RemoteSubagentDescriptor.AgentSessionId),
            nameof(RemoteSubagentDescriptor.AgentId),
            nameof(RemoteSubagentDescriptor.OwningProfileEntityId),
            nameof(RemoteSubagentDescriptor.OwnershipGeneration),
            nameof(RemoteSubagentDescriptor.RuntimeEpoch));
    }

    [Fact]
    public void ProtocolDtos_OptionalProperties_UseDocumentedDefaults()
    {
        var open = Open();
        Assert.Null(open.ReplayCursor);
        var move = new MoveQueueItemCommand
        {
            ExpectedRevision = 0, SourceQueueId = "a", ItemId = "i", TargetQueueId = "b",
            CommandId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(), RuntimeEpoch = Epoch(),
        };
        Assert.Null(move.BeforeItemId);
    }

    [Theory]
    [InlineData(AgentSessionOpenIntent.Status, "status")]
    [InlineData(AgentSessionOpenIntent.Start, "start")]
    [InlineData(AgentSessionOpenIntent.Attach, "attach")]
    [InlineData(AgentSessionOpenIntent.StartOrAttach, "start-or-attach")]
    [InlineData(AgentSessionOpenIntent.Resume, "resume")]
    public void Serialize_AllOpenIntents_UsesVersionOneDiscriminators(
        AgentSessionOpenIntent intent, string expected)
    {
        var json = AgentSessionProtocolCodec.SerializeOpen(Open() with { OpenIntent = intent });
        Assert.Equal("attach-agent-session", json.GetProperty("type").GetString());
        Assert.Equal(1, json.GetProperty("protocol-version").GetInt32());
        Assert.Equal(expected, json.GetProperty("open-intent").GetString());
        Assert.Equal(intent, AgentSessionProtocolCodec.DeserializeOpen(json).OpenIntent);
    }

    [Fact]
    public void AgentSessionOpenRequest_InvalidVersionOrGeneration_RejectsInitialization()
    {
        Assert.Throws<RemoteAgentProtocolException>(() =>
            AgentSessionProtocolCodec.SerializeOpen(Open() with { ProtocolVersion = 2 }));
        Assert.Throws<RemoteAgentProtocolException>(() =>
            AgentSessionProtocolCodec.SerializeOpen(Open() with { ExpectedOwnershipGeneration = -1 }));
    }

    [Fact]
    public void Deserialize_UnknownMember_RejectsFrame()
    {
        var open = AgentSessionProtocolCodec.SerializeOpen(Open());
        var json = open.GetRawText().TrimEnd('}') + ",\"compiled-policy\":{}}";
        Assert.Throws<RemoteAgentProtocolException>(() =>
            AgentSessionProtocolCodec.DeserializeOpen(JsonDocument.Parse(json).RootElement));
    }

    [Fact]
    public void RoundTrip_AllCommandDiscriminators_PreservesIdsEpochAndPayload()
    {
        var command = new MoveQueueItemCommand
        {
            ExpectedRevision = 42, SourceQueueId = "source", ItemId = "item",
            TargetQueueId = "target", BeforeItemId = "before",
            CommandId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(), RuntimeEpoch = Epoch(),
        };
        var copy = Assert.IsType<MoveQueueItemCommand>(
            AgentSessionProtocolCodec.DeserializeCommand(AgentSessionProtocolCodec.SerializeCommand(command)));
        Assert.Equal(command.CommandId, copy.CommandId);
        Assert.Equal(command.CorrelationId, copy.CorrelationId);
        Assert.Equal("before", copy.BeforeItemId);
        Assert.Equal(42, copy.ExpectedRevision);
    }

    [Fact]
    public void AgentSessionServerFrame_Type_IsCodecAssignedFromEventDiscriminator()
    {
        Assert.False(typeof(AgentSessionServerFrame).GetProperty(nameof(AgentSessionServerFrame.Type))!
            .SetMethod!.IsPublic);
        var frame = AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.CreateFrame(
            Epoch(), 1, Guid.NewGuid(), new BusyChangedEvent { IsBusy = true });
        Assert.Equal("busy-changed", frame.Type);
        var copy = AgentSessionProtocolCodec.DeserializeFrame(
            AgentSessionProtocolCodec.SerializeFrame(frame));
        Assert.True(Assert.IsType<BusyChangedEvent>(
            AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.Deserialize(copy)).IsBusy);
    }

    [Fact]
    public void RoundTrip_SessionSnapshot_PreservesUsageInformationAndFullQueues()
    {
        var snapshot = Snapshot();
        var frame = AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.CreateFrame(
            Epoch(), 1, Guid.NewGuid(), new SessionSnapshotEvent { Snapshot = snapshot });
        var copy = Assert.IsType<SessionSnapshotEvent>(AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.Deserialize(
            AgentSessionProtocolCodec.DeserializeFrame(AgentSessionProtocolCodec.SerializeFrame(frame))));
        Assert.Equal("session", copy.Snapshot.Information.AgentSessionId);
        Assert.Equal("test-agent", copy.Snapshot.Information.AgentDefinition.Name);
        Assert.Equal(7, copy.Snapshot.Usage.TotalInputTokenCount);
        Assert.Equal(2, copy.Snapshot.InputQueues.Queues.Length);
    }

    [Fact]
    public void RoundTrip_SessionRetentionChanged_PreservesPreferenceAndViewerCount()
    {
        var frame = AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.CreateFrame(Epoch(), 1, Guid.NewGuid(),
            new SessionRetentionChangedEvent { ContinueInBackground = true, ViewerCount = 3 });
        var copy = Assert.IsType<SessionRetentionChangedEvent>(AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.Deserialize(
            AgentSessionProtocolCodec.DeserializeFrame(AgentSessionProtocolCodec.SerializeFrame(frame))));
        Assert.True(copy.ContinueInBackground);
        Assert.Equal(3, copy.ViewerCount);
    }

    internal static AgentSessionOpenRequest Open() => new()
    {
        ProtocolVersion = 1,
        AgentSessionId = "session",
        ExpectedOwningProfileEntityId = "profile",
        ExpectedOwnershipGeneration = 2,
        OpenIntent = AgentSessionOpenIntent.Attach,
        AttachmentToken = "00112233445566778899aabbccddeeff",
        Capabilities = ["queue", "replay"],
    };

    internal static RuntimeEpoch Epoch() => new() { Value = Guid.Parse("11111111-1111-1111-1111-111111111111") };

    internal static AgentSessionSnapshot Snapshot() => new()
    {
        Information = new AgentInformation
        {
            AgentSessionId = "session", AgentId = "agent", Name = "test-agent",
            DisplayName = "Test agent", Description = "A test agent", AcceptsUserInput = true,
            CurrentModelId = "echo", AgentDefinition = MakeDefinition(),
        },
        Usage = new Usage { TotalInputTokenCount = 7, TotalSessionCostUsd = 0.1 },
        InputQueues = new AgentInputQueuesSnapshot
        {
            Revision = 1,
            Queues =
            [
                Queue("immediate", true, false),
                Queue("default", false, true),
            ],
        },
        IsBusy = false,
        History = [], RunningItems = [], Tools = [], Subagents = [], Modals = [],
        ContinueInBackground = false, ViewerCount = 1,
    };

    private static AgentInputQueueSnapshot Queue(string id, bool immediate, bool isDefault) => new()
    {
        QueueId = id, Name = id, IsDefault = isDefault, IsImmediate = immediate,
        Immediacy = immediate ? AgentInputQueueImmediacy.Immediate : AgentInputQueueImmediacy.Queue,
        Priority = 0, Revision = 0, Items = ImmutableArray<AgentInputItemSnapshot>.Empty,
    };

    private static AgentDefinition MakeDefinition() => AgentDefinitionLoader.LoadAgentFromJson("""
    {
      "kind": "prompt",
      "name": "test-agent",
      "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
    }
    """);

    private static void AssertRequired<T>(params string[] propertyNames)
    {
        foreach (var name in propertyNames)
            Assert.NotNull(typeof(T).GetProperty(name)!.GetCustomAttribute<System.Runtime.CompilerServices.RequiredMemberAttribute>());
    }
}
