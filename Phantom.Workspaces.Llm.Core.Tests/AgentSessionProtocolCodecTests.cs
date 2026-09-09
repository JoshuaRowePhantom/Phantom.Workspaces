using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
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
        AssertRequired<RuntimeEpoch>(nameof(RuntimeEpoch.Value));
        AssertRequired<ReplayCursor>(nameof(ReplayCursor.Epoch), nameof(ReplayCursor.Sequence));
        AssertRequired<AgentSessionServerFrame>(
            nameof(AgentSessionServerFrame.ProtocolVersion),
            nameof(AgentSessionServerFrame.CorrelationId),
            nameof(AgentSessionServerFrame.RuntimeEpoch),
            nameof(AgentSessionServerFrame.Sequence),
            nameof(AgentSessionServerFrame.Payload));
        AssertRequired<AgentSessionTakeoverRequest>(
            nameof(AgentSessionTakeoverRequest.AgentSessionId),
            nameof(AgentSessionTakeoverRequest.ExpectedOwningProfileEntityId),
            nameof(AgentSessionTakeoverRequest.ExpectedOwnershipGeneration),
            nameof(AgentSessionTakeoverRequest.NewOwningProfileEntityId),
            nameof(AgentSessionTakeoverRequest.CorrelationId));
        AssertRequired<RemoteAgentOperationError>(
            nameof(RemoteAgentOperationError.Code), nameof(RemoteAgentOperationError.Operation),
            nameof(RemoteAgentOperationError.IsRetryable), nameof(RemoteAgentOperationError.Message),
            nameof(RemoteAgentOperationError.CorrelationId));

        AssertCommandRequired<CreateQueueCommand>("ExpectedRevision", "Configuration");
        AssertCommandRequired<DeleteQueueCommand>("ExpectedRevision", "QueueId");
        AssertCommandRequired<EnqueueInputCommand>("ExpectedRevision", "TargetQueueId", "Messages");
        AssertCommandRequired<EditQueueItemCommand>("ExpectedRevision", "QueueId", "ItemId", "Messages");
        AssertCommandRequired<RemoveQueueItemCommand>("ExpectedRevision", "QueueId", "ItemId");
        AssertCommandRequired<MoveQueueItemCommand>("ExpectedRevision", "SourceQueueId", "ItemId", "TargetQueueId");
        AssertCommandRequired<ConfigureQueueCommand>("ExpectedRevision", "QueueId", "Configuration");
        AssertCommandRequired<InterruptCommand>();
        AssertCommandRequired<TerminateSessionCommand>("Reason");
        AssertCommandRequired<OpenSubagentCommand>("AgentId");
        AssertCommandRequired<ModalResponseCommand>("ModalId", "Response");
        AssertCommandRequired<SetToolEnabledCommand>("ToolId", "Enabled");
        AssertCommandRequired<SetContinueInBackgroundCommand>("ContinueInBackground");
        AssertCommandRequired<DetachCommand>();

        AssertRequired<SessionStatusEvent>("Status");
        AssertRequired<SessionSnapshotEvent>("Snapshot");
        AssertRequired<HistoryAppendedEvent>("Item");
        AssertRequired<UsageChangedEvent>("Usage");
        AssertRequired<AgentInformationChangedEvent>("Information");
        AssertRequired<QueueChangedEvent>("Revision", "Queues", "RemovedQueueIds");
        AssertRequired<StreamingStartedEvent>("RunId", "Item");
        AssertRequired<StreamingUpdatedEvent>("RunId", "Update");
        AssertRequired<StreamingCompletedEvent>("RunId", "Item");
        AssertRequired<BusyChangedEvent>("IsBusy");
        AssertRequired<ToolsSnapshotEvent>("Tools");
        AssertRequired<ToolsChangedEvent>("Tools");
        AssertRequired<SubagentsSnapshotEvent>("Subagents");
        AssertRequired<SubagentsChangedEvent>("Subagents");
        AssertRequired<ModalRaisedEvent>("Modal");
        AssertRequired<ModalUpdatedEvent>("Modal");
        AssertRequired<ModalDismissedEvent>("ModalId");
        AssertRequired<SessionRetentionChangedEvent>("ContinueInBackground", "ViewerCount");
        AssertRequired<CommandCompletedEvent>("CommandId");
        AssertRequired<OperationErrorEvent>("Error");
        AssertRequired<SessionTerminalEvent>("Reason", "CompletionState");
        AssertRequired<AgentSessionSnapshot>(
            "Information", "Usage", "InputQueues", "IsBusy", "History", "RunningItems",
            "Tools", "Subagents", "Modals", "ContinueInBackground", "ViewerCount");
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

    [Fact]
    public void ProtocolDtos_NamedInitializers_PreserveFixedDiscriminators()
    {
        AgentSessionCommand[] commands =
        [
            new InterruptCommand { CommandId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(), RuntimeEpoch = Epoch() },
            new DetachCommand { CommandId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(), RuntimeEpoch = Epoch() },
            new SetContinueInBackgroundCommand
            {
                ContinueInBackground = true, CommandId = Guid.NewGuid(),
                CorrelationId = Guid.NewGuid(), RuntimeEpoch = Epoch(),
            },
        ];

        Assert.Equal(["interrupt", "detach", "set-continue-in-background"], commands.Select(c => c.Type));
    }

    [Fact]
    public void RemoteSubagentDescriptor_ValidValues_RoundTrips()
    {
        var value = new RemoteSubagentDescriptor
        {
            AgentSessionId = "child", AgentId = "agent", OwningProfileEntityId = "profile",
            OwnershipGeneration = 4, RuntimeEpoch = Epoch(),
        };
        var copy = JsonSerializer.Deserialize<RemoteSubagentDescriptor>(
            JsonSerializer.Serialize(value, AgentSessionProtocolCodec.Options),
            AgentSessionProtocolCodec.Options)!;
        Assert.Equal(value, copy);
    }

    [Fact]
    public void RoundTrip_AgentSessionTakeoverRequest_PreservesProfilesGenerationAndCorrelation()
    {
        var value = new AgentSessionTakeoverRequest
        {
            AgentSessionId = "session", ExpectedOwningProfileEntityId = "old",
            ExpectedOwnershipGeneration = 8, NewOwningProfileEntityId = "new",
            CorrelationId = Guid.NewGuid(),
        };
        var copy = JsonSerializer.Deserialize<AgentSessionTakeoverRequest>(
            JsonSerializer.Serialize(value, AgentSessionProtocolCodec.Options),
            AgentSessionProtocolCodec.Options)!;
        Assert.Equal(value, copy);
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
        var json = JsonDocument.Parse("""[{"value":"payload"}]""").RootElement.Clone();
        var configuration = new AgentInputQueueConfiguration
        { Name = "queue", Immediacy = AgentInputQueueImmediacy.Queue, Priority = 3 };
        static (Guid CommandId, Guid CorrelationId, RuntimeEpoch Epoch) Identity()
            => (Guid.NewGuid(), Guid.NewGuid(), Epoch());
        var i = Identity();
        AgentSessionCommand[] commands =
        [
            new CreateQueueCommand
            { ExpectedRevision = 1, Configuration = configuration, CommandId = (i = Identity()).CommandId, CorrelationId = i.CorrelationId, RuntimeEpoch = i.Epoch },
            new DeleteQueueCommand
            { ExpectedRevision = 2, QueueId = "queue", CommandId = (i = Identity()).CommandId, CorrelationId = i.CorrelationId, RuntimeEpoch = i.Epoch },
            new EnqueueInputCommand
            { ExpectedRevision = 3, TargetQueueId = "queue", Messages = json, CommandId = (i = Identity()).CommandId, CorrelationId = i.CorrelationId, RuntimeEpoch = i.Epoch },
            new EditQueueItemCommand
            { ExpectedRevision = 4, QueueId = "queue", ItemId = "item", Messages = json, CommandId = (i = Identity()).CommandId, CorrelationId = i.CorrelationId, RuntimeEpoch = i.Epoch },
            new RemoveQueueItemCommand
            { ExpectedRevision = 5, QueueId = "queue", ItemId = "item", CommandId = (i = Identity()).CommandId, CorrelationId = i.CorrelationId, RuntimeEpoch = i.Epoch },
            new MoveQueueItemCommand
            {
                ExpectedRevision = 6, SourceQueueId = "source", ItemId = "item",
                TargetQueueId = "target", BeforeItemId = "before",
                CommandId = (i = Identity()).CommandId, CorrelationId = i.CorrelationId, RuntimeEpoch = i.Epoch,
            },
            new ConfigureQueueCommand
            { ExpectedRevision = 7, QueueId = "queue", Configuration = configuration, CommandId = (i = Identity()).CommandId, CorrelationId = i.CorrelationId, RuntimeEpoch = i.Epoch },
            new InterruptCommand
            { CommandId = (i = Identity()).CommandId, CorrelationId = i.CorrelationId, RuntimeEpoch = i.Epoch },
            new TerminateSessionCommand
            { Reason = "done", CommandId = (i = Identity()).CommandId, CorrelationId = i.CorrelationId, RuntimeEpoch = i.Epoch },
            new OpenSubagentCommand
            { AgentId = "agent", CommandId = (i = Identity()).CommandId, CorrelationId = i.CorrelationId, RuntimeEpoch = i.Epoch },
            new ModalResponseCommand
            { ModalId = "modal", Response = json, CommandId = (i = Identity()).CommandId, CorrelationId = i.CorrelationId, RuntimeEpoch = i.Epoch },
            new SetToolEnabledCommand
            { ToolId = "tool", Enabled = true, CommandId = (i = Identity()).CommandId, CorrelationId = i.CorrelationId, RuntimeEpoch = i.Epoch },
            new SetContinueInBackgroundCommand
            { ContinueInBackground = true, CommandId = (i = Identity()).CommandId, CorrelationId = i.CorrelationId, RuntimeEpoch = i.Epoch },
            new DetachCommand
            { CommandId = (i = Identity()).CommandId, CorrelationId = i.CorrelationId, RuntimeEpoch = i.Epoch },
        ];

        foreach (var command in commands)
        {
            var encoded = AgentSessionProtocolCodec.SerializeCommand(command);
            var copy = AgentSessionProtocolCodec.DeserializeCommand(encoded);
            Assert.IsType(command.GetType(), copy);
            Assert.Equal(command.Type, copy.Type);
            Assert.Equal(command.CommandId, copy.CommandId);
            Assert.Equal(command.CorrelationId, copy.CorrelationId);
            Assert.Equal(encoded.GetRawText(), AgentSessionProtocolCodec.SerializeCommand(copy).GetRawText());
        }
        Assert.Equal(14, commands.Select(command => command.Type).Distinct().Count());
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

    [Fact]
    public void RoundTrip_AllServerEventDiscriminators_PreservesSequenceAndCorrelation()
    {
        long sequence = 0;
        foreach (var value in ServerEvents())
        {
            var correlation = Guid.NewGuid();
            var frame = AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.CreateFrame(
                Epoch(), ++sequence, correlation, value);
            var copy = AgentSessionProtocolCodec.DeserializeFrame(AgentSessionProtocolCodec.SerializeFrame(frame));
            Assert.Equal(value.Type, copy.Type);
            Assert.Equal(sequence, copy.Sequence);
            Assert.Equal(correlation, copy.CorrelationId);
            Assert.IsType(value.GetType(), AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.Deserialize(copy));
        }
        Assert.Equal(21, sequence);
    }

    [Fact]
    public void RoundTrip_SessionSnapshot_PreservesBackgroundPreferenceViewerCountAndFullDefinition()
    {
        var snapshot = Snapshot() with { ContinueInBackground = true, ViewerCount = 3 };
        var copy = RoundTrip(new SessionSnapshotEvent { Snapshot = snapshot });
        Assert.True(copy.Snapshot.ContinueInBackground);
        Assert.Equal(3, copy.Snapshot.ViewerCount);
        Assert.Equal(snapshot.Information.AgentDefinition.ToJson(), copy.Snapshot.Information.AgentDefinition.ToJson());
    }

    [Fact]
    public void RoundTrip_SetContinueInBackgroundCommand_PreservesCommandAndCorrelationIds()
    {
        var value = new SetContinueInBackgroundCommand
        {
            ContinueInBackground = true, CommandId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(), RuntimeEpoch = Epoch(),
        };
        var copy = Assert.IsType<SetContinueInBackgroundCommand>(
            AgentSessionProtocolCodec.DeserializeCommand(AgentSessionProtocolCodec.SerializeCommand(value)));
        Assert.True(copy.ContinueInBackground);
        Assert.Equal(value.CommandId, copy.CommandId);
        Assert.Equal(value.CorrelationId, copy.CorrelationId);
    }

    [Fact]
    public void RoundTrip_QueueChanged_PreservesStableIdsRevisionsAndOrdering()
    {
        var queues = Snapshot().InputQueues.Queues.Reverse().ToArray();
        var copy = RoundTrip(new QueueChangedEvent
        {
            Revision = 9, Queues = queues, RemovedQueueIds = ["removed"],
        });
        Assert.Equal(9, copy.Revision);
        Assert.Equal(queues.Select(q => q.QueueId), copy.Queues.Select(q => q.QueueId));
        Assert.Equal(["removed"], copy.RemovedQueueIds);
    }

    [Fact]
    public void RoundTrip_AgentInformation_ClonesDefinitionJsonElements()
    {
        var copy = RoundTrip(new AgentInformationChangedEvent { Information = Snapshot().Information });
        Assert.Equal("test-agent", copy.Information.AgentDefinition.Name);
        Assert.Equal("prompt", JsonDocument.Parse(copy.Information.AgentDefinition.ToJson()).RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public void Deserialize_CompiledPolicyMember_RejectsFrame()
    {
        var node = JsonNode.Parse(AgentSessionProtocolCodec.SerializeFrame(
            AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.CreateFrame(
                Epoch(), 1, Guid.NewGuid(),
                new AgentInformationChangedEvent { Information = Snapshot().Information })).GetRawText())!;
        node["payload"]!["information"]!["compiled-policy"] = new JsonObject();
        Assert.Throws<RemoteAgentProtocolException>(() =>
            AgentSessionProtocolCodec.DeserializeFrame(JsonSerializer.SerializeToElement(node)));
    }

    [Fact]
    public void Deserialize_EmptyRequiredId_RejectsFrame()
    {
        var frame = AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.CreateFrame(
            Epoch(), 1, Guid.NewGuid(), new ModalDismissedEvent { ModalId = "modal" });
        var node = JsonNode.Parse(AgentSessionProtocolCodec.SerializeFrame(frame).GetRawText())!;
        node["payload"]!["modal-id"] = "";
        Assert.Throws<ArgumentException>(() =>
            AgentSessionProtocolCodec.DeserializeFrame(JsonSerializer.SerializeToElement(node)));
    }

    [Fact]
    public async Task ServerFrames_ConcurrentPublish_AreStrictlyOrdered()
    {
        var channel = new PublisherChannel();
        var publisher = new AgentSessionFramePublisher(channel, Epoch());
        var publishes = Enumerable.Range(0, 128)
            .Select(index => Task.Run(() => publisher.PublishAsync(
                new BusyChangedEvent { IsBusy = index % 2 == 0 }, Guid.NewGuid())))
            .ToArray();
        await Task.WhenAll(publishes);

        var delivered = new List<long>();
        while (channel.Outgoing.Reader.TryRead(out var raw))
            delivered.Add(AgentSessionProtocolCodec.DeserializeFrame(raw).Sequence);
        Assert.Equal(Enumerable.Range(1, 128).Select(index => (long)index), delivered);
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

    private static T RoundTrip<T>(T value) where T : AgentSessionServerEvent
        => Assert.IsType<T>(AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.Deserialize(
            AgentSessionProtocolCodec.DeserializeFrame(AgentSessionProtocolCodec.SerializeFrame(
                AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.CreateFrame(
                    Epoch(), 1, Guid.NewGuid(), value)))));

    private static IEnumerable<AgentSessionServerEvent> ServerEvents()
    {
        var json = JsonDocument.Parse("""{"value":"x"}""").RootElement.Clone();
        var commandId = Guid.NewGuid();
        var modal = new AgentChatModal
        {
            Id = "modal", OwnerAgentId = "agent", Title = "Title", Body = "Body",
            Content = new FreeformModalContent { IsRequired = false },
        };
        yield return new SessionStatusEvent { Status = AgentSessionRemoteStatus.Running };
        yield return new SessionSnapshotEvent { Snapshot = Snapshot() };
        yield return new HistoryAppendedEvent { Item = json };
        yield return new UsageChangedEvent { Usage = new Usage() };
        yield return new AgentInformationChangedEvent { Information = Snapshot().Information };
        yield return new QueueChangedEvent { Revision = 2, Queues = Snapshot().InputQueues.Queues, RemovedQueueIds = [] };
        yield return new StreamingStartedEvent { RunId = "run", Item = json };
        yield return new StreamingUpdatedEvent { RunId = "run", Update = json };
        yield return new StreamingCompletedEvent { RunId = "run", Item = json };
        yield return new BusyChangedEvent { IsBusy = true };
        yield return new ToolsSnapshotEvent { Tools = [json] };
        yield return new ToolsChangedEvent { Tools = [json] };
        yield return new SubagentsSnapshotEvent { Subagents = [json] };
        yield return new SubagentsChangedEvent { Subagents = [json] };
        yield return new ModalRaisedEvent { Modal = modal };
        yield return new ModalUpdatedEvent { Modal = modal };
        yield return new ModalDismissedEvent { ModalId = "modal" };
        yield return new SessionRetentionChangedEvent { ContinueInBackground = true, ViewerCount = 1 };
        yield return new CommandCompletedEvent { CommandId = commandId };
        yield return new OperationErrorEvent
        {
            Error = new RemoteAgentOperationError
            {
                Code = "cancelled", Operation = "test", IsRetryable = true,
                Message = "Cancelled.", CorrelationId = Guid.NewGuid(),
            },
        };
        yield return new SessionTerminalEvent { Reason = "done", CompletionState = json };
    }

    private static void AssertRequired<T>(params string[] propertyNames)
    {
        foreach (var name in propertyNames)
            Assert.NotNull(typeof(T).GetProperty(name)!.GetCustomAttribute<System.Runtime.CompilerServices.RequiredMemberAttribute>());
    }

    private static void AssertCommandRequired<T>(params string[] payloadPropertyNames)
        where T : AgentSessionCommand
        => AssertRequired<T>(
            [nameof(AgentSessionCommand.CommandId), nameof(AgentSessionCommand.CorrelationId),
             nameof(AgentSessionCommand.RuntimeEpoch), .. payloadPropertyNames]);

    private sealed class PublisherChannel : IMessageChannel
    {
        public Channel<JsonElement> Outgoing { get; } = Channel.CreateUnbounded<JsonElement>();
        public ChannelWriter<JsonElement> Writer => this.Outgoing.Writer;
        public ChannelReader<JsonElement> Reader => Channel.CreateUnbounded<JsonElement>().Reader;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
