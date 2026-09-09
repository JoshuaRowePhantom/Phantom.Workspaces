using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentSchema;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Llm.Remote;

public readonly record struct RuntimeEpoch
{
    private readonly Guid value;
    public required Guid Value
    {
        get => this.value;
        init => this.value = value != Guid.Empty ? value : throw new ArgumentException("Runtime epoch cannot be empty.", nameof(Value));
    }
}

public readonly record struct ReplayCursor
{
    private readonly long sequence;
    public required RuntimeEpoch Epoch { get; init; }
    public required long Sequence
    {
        get => this.sequence;
        init => this.sequence = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(Sequence));
    }
}

public enum AgentSessionOpenIntent { Status, Start, Attach, StartOrAttach, Resume }
public enum AgentSessionRemoteStatus { Running, NotRunning, Unavailable }

public sealed record AgentSessionOpenRequest
{
    public required int ProtocolVersion { get; init; }
    public required string AgentSessionId { get; init; }
    public required string ExpectedOwningProfileEntityId { get; init; }
    public required long ExpectedOwnershipGeneration { get; init; }
    public required AgentSessionOpenIntent OpenIntent { get; init; }
    public required string AttachmentToken { get; init; }
    public ReplayCursor? ReplayCursor { get; init; }
    public required IReadOnlyList<string> Capabilities { get; init; }
}

public sealed record AgentSessionStatusRequest
{
    public required Phantom.Workspaces.Transport.ITransport Transport { get; init; }
    public required AgentSessionOpenRequest OpenRequest { get; init; }
}

public sealed record TerminateAgentSessionRequest
{
    public required string Reason { get; init; }
    public required Guid CommandId { get; init; }
}

public sealed record OpenAgentSubagentRequest
{
    public required string AgentId { get; init; }
    public required Guid CommandId { get; init; }
}

public sealed record RespondToAgentModalRequest
{
    public required string ModalId { get; init; }
    public required JsonElement Response { get; init; }
    public required Guid CommandId { get; init; }
}

public sealed record SetAgentToolEnabledRequest
{
    public required string ToolId { get; init; }
    public required bool Enabled { get; init; }
    public required Guid CommandId { get; init; }
}

public sealed record SetAgentSessionRetentionRequest
{
    public required bool ContinueInBackground { get; init; }
    public required Guid CommandId { get; init; }
}

public sealed record RemoteSubagentDescriptor
{
    public required string AgentSessionId { get; init; }
    public required string AgentId { get; init; }
    public required string OwningProfileEntityId { get; init; }
    public required long OwnershipGeneration { get; init; }
    public required RuntimeEpoch RuntimeEpoch { get; init; }
}

public sealed record AgentSessionServerFrame
{
    public required int ProtocolVersion { get; init; }
    public string Type { get; internal init; } = null!;
    public required Guid CorrelationId { get; init; }
    public required RuntimeEpoch RuntimeEpoch { get; init; }
    public required long Sequence { get; init; }
    public required JsonElement Payload { get; init; }
}

internal abstract record AgentSessionCommand
{
    public abstract string Type { get; }
    public required Guid CommandId { get; init; }
    public required Guid CorrelationId { get; init; }
    public required RuntimeEpoch RuntimeEpoch { get; init; }
}

internal sealed record CreateQueueCommand : AgentSessionCommand
{
    public override string Type => "create-queue";
    public required long ExpectedRevision { get; init; }
    public required AgentInputQueueConfiguration Configuration { get; init; }
}
internal sealed record DeleteQueueCommand : AgentSessionCommand
{
    public override string Type => "delete-queue";
    public required long ExpectedRevision { get; init; }
    public required string QueueId { get; init; }
}
internal sealed record EnqueueInputCommand : AgentSessionCommand
{
    public override string Type => "enqueue-input";
    public required long ExpectedRevision { get; init; }
    public required string TargetQueueId { get; init; }
    public required JsonElement Messages { get; init; }
}
internal sealed record EditQueueItemCommand : AgentSessionCommand
{
    public override string Type => "edit-queue-item";
    public required long ExpectedRevision { get; init; }
    public required string QueueId { get; init; }
    public required string ItemId { get; init; }
    public required JsonElement Messages { get; init; }
}
internal sealed record RemoveQueueItemCommand : AgentSessionCommand
{
    public override string Type => "remove-queue-item";
    public required long ExpectedRevision { get; init; }
    public required string QueueId { get; init; }
    public required string ItemId { get; init; }
}
internal sealed record MoveQueueItemCommand : AgentSessionCommand
{
    public override string Type => "move-queue-item";
    public required long ExpectedRevision { get; init; }
    public required string SourceQueueId { get; init; }
    public required string ItemId { get; init; }
    public required string TargetQueueId { get; init; }
    public string? BeforeItemId { get; init; }
}
internal sealed record ConfigureQueueCommand : AgentSessionCommand
{
    public override string Type => "configure-queue";
    public required long ExpectedRevision { get; init; }
    public required string QueueId { get; init; }
    public required AgentInputQueueConfiguration Configuration { get; init; }
}
internal sealed record InterruptCommand : AgentSessionCommand { public override string Type => "interrupt"; }
internal sealed record TerminateSessionCommand : AgentSessionCommand
{
    public override string Type => "terminate-session";
    public required string Reason { get; init; }
}
internal sealed record OpenSubagentCommand : AgentSessionCommand
{
    public override string Type => "open-subagent";
    public required string AgentId { get; init; }
}
internal sealed record ModalResponseCommand : AgentSessionCommand
{
    public override string Type => "modal-response";
    public required string ModalId { get; init; }
    public required JsonElement Response { get; init; }
}
internal sealed record SetToolEnabledCommand : AgentSessionCommand
{
    public override string Type => "set-tool-enabled";
    public required string ToolId { get; init; }
    public required bool Enabled { get; init; }
}
internal sealed record SetContinueInBackgroundCommand : AgentSessionCommand
{
    public override string Type => "set-continue-in-background";
    public required bool ContinueInBackground { get; init; }
}
internal sealed record DetachCommand : AgentSessionCommand { public override string Type => "detach"; }

internal abstract record AgentSessionServerEvent { public abstract string Type { get; } }
internal sealed record SessionStatusEvent : AgentSessionServerEvent
{
    public override string Type => "session-status";
    public required AgentSessionRemoteStatus Status { get; init; }
}
internal sealed record SessionSnapshotEvent : AgentSessionServerEvent
{
    public override string Type => "session-snapshot";
    public required AgentSessionSnapshot Snapshot { get; init; }
}
internal sealed record HistoryAppendedEvent : AgentSessionServerEvent
{
    public override string Type => "history-appended";
    public required JsonElement Item { get; init; }
}
internal sealed record UsageChangedEvent : AgentSessionServerEvent
{
    public override string Type => "usage-changed";
    public required Usage Usage { get; init; }
}
internal sealed record AgentInformationChangedEvent : AgentSessionServerEvent
{
    public override string Type => "agent-information-changed";
    public required AgentInformation Information { get; init; }
}
internal sealed record QueueChangedEvent : AgentSessionServerEvent
{
    public override string Type => "queue-changed";
    public required long Revision { get; init; }
    public required IReadOnlyList<AgentInputQueueSnapshot> Queues { get; init; }
    public required IReadOnlyList<string> RemovedQueueIds { get; init; }
}
internal sealed record StreamingStartedEvent : AgentSessionServerEvent
{
    public override string Type => "streaming-started";
    public required string RunId { get; init; }
    public required JsonElement Item { get; init; }
}
internal sealed record StreamingUpdatedEvent : AgentSessionServerEvent
{
    public override string Type => "streaming-updated";
    public required string RunId { get; init; }
    public required JsonElement Update { get; init; }
}
internal sealed record StreamingCompletedEvent : AgentSessionServerEvent
{
    public override string Type => "streaming-completed";
    public required string RunId { get; init; }
    public required JsonElement Item { get; init; }
}
internal sealed record BusyChangedEvent : AgentSessionServerEvent
{
    public override string Type => "busy-changed";
    public required bool IsBusy { get; init; }
}
internal sealed record ToolsSnapshotEvent : AgentSessionServerEvent
{
    public override string Type => "tools-snapshot";
    public required IReadOnlyList<JsonElement> Tools { get; init; }
}
internal sealed record ToolsChangedEvent : AgentSessionServerEvent
{
    public override string Type => "tools-changed";
    public required IReadOnlyList<JsonElement> Tools { get; init; }
}
internal sealed record SubagentsSnapshotEvent : AgentSessionServerEvent
{
    public override string Type => "subagents-snapshot";
    public required IReadOnlyList<JsonElement> Subagents { get; init; }
}
internal sealed record SubagentsChangedEvent : AgentSessionServerEvent
{
    public override string Type => "subagents-changed";
    public required IReadOnlyList<JsonElement> Subagents { get; init; }
}
internal sealed record ModalRaisedEvent : AgentSessionServerEvent
{
    public override string Type => "modal-raised";
    public required AgentChatModal Modal { get; init; }
}
internal sealed record ModalUpdatedEvent : AgentSessionServerEvent
{
    public override string Type => "modal-updated";
    public required AgentChatModal Modal { get; init; }
}
internal sealed record ModalDismissedEvent : AgentSessionServerEvent
{
    public override string Type => "modal-dismissed";
    public required string ModalId { get; init; }
}
internal sealed record SessionRetentionChangedEvent : AgentSessionServerEvent
{
    public override string Type => "session-retention-changed";
    public required bool ContinueInBackground { get; init; }
    public required int ViewerCount { get; init; }
}
internal sealed record CommandCompletedEvent : AgentSessionServerEvent
{
    public override string Type => "command-completed";
    public required Guid CommandId { get; init; }
    public JsonElement? Result { get; init; }
}
internal sealed record OperationErrorEvent : AgentSessionServerEvent
{
    public override string Type => "operation-error";
    public required RemoteAgentOperationError Error { get; init; }
}
internal sealed record SessionTerminalEvent : AgentSessionServerEvent
{
    public override string Type => "session-terminal";
    public required string Reason { get; init; }
    public required JsonElement CompletionState { get; init; }
}

internal sealed record AgentSessionSnapshot
{
    public required AgentInformation Information { get; init; }
    public required Usage Usage { get; init; }
    public required AgentInputQueuesSnapshot InputQueues { get; init; }
    public required bool IsBusy { get; init; }
    public required IReadOnlyList<JsonElement> History { get; init; }
    public required IReadOnlyList<JsonElement> RunningItems { get; init; }
    public required IReadOnlyList<JsonElement> Tools { get; init; }
    public required IReadOnlyList<JsonElement> Subagents { get; init; }
    public required IReadOnlyList<AgentChatModal> Modals { get; init; }
    public required bool ContinueInBackground { get; init; }
    public required int ViewerCount { get; init; }
    public JsonElement? CompletionState { get; init; }
}

internal sealed record AgentSessionTakeoverRequest
{
    public required string AgentSessionId { get; init; }
    public required string ExpectedOwningProfileEntityId { get; init; }
    public required long ExpectedOwnershipGeneration { get; init; }
    public required string NewOwningProfileEntityId { get; init; }
    public required Guid CorrelationId { get; init; }
}

internal sealed record RemoteAgentOperationError
{
    public required string Code { get; init; }
    public required string Operation { get; init; }
    public required bool IsRetryable { get; init; }
    public required string Message { get; init; }
    public required Guid CorrelationId { get; init; }
}

internal static class AgentSessionProtocolCodec
{
    private const int Version = 1;
    private static readonly HashSet<string> KnownCapabilities = new(StringComparer.Ordinal)
    {
        "queue", "tools", "modals", "subagents", "background", "replay",
    };
    internal static readonly JsonSerializerOptions Options = CreateOptions();

    internal static JsonElement SerializeOpen(AgentSessionOpenRequest request)
    {
        ValidateOpen(request);
        var value = JsonSerializer.SerializeToElement(request, Options);
        var properties = value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        properties["type"] = JsonSerializer.SerializeToElement("attach-agent-session");
        return JsonSerializer.SerializeToElement(properties, Options);
    }

    internal static AgentSessionOpenRequest DeserializeOpen(JsonElement element)
    {
        RequireObjectAndType(element, "attach-agent-session", OpenNames);
        var members = element.EnumerateObject()
            .Where(p => p.Name != "type")
            .ToDictionary(p => p.Name, p => p.Value.Clone());
        var request = JsonSerializer.Deserialize<AgentSessionOpenRequest>(
            JsonSerializer.Serialize(members, Options), Options)
            ?? throw Protocol("Open request was null.");
        ValidateOpen(request);
        return request;
    }

    internal static JsonElement SerializeCommand(AgentSessionCommand command)
    {
        ValidateCommand(command);
        return JsonSerializer.SerializeToElement(command, command.GetType(), Options);
    }

    internal static AgentSessionCommand DeserializeCommand(JsonElement element)
    {
        var type = ReadType(element);
        var target = type switch
        {
            "create-queue" => typeof(CreateQueueCommand),
            "delete-queue" => typeof(DeleteQueueCommand),
            "enqueue-input" => typeof(EnqueueInputCommand),
            "edit-queue-item" => typeof(EditQueueItemCommand),
            "remove-queue-item" => typeof(RemoveQueueItemCommand),
            "move-queue-item" => typeof(MoveQueueItemCommand),
            "configure-queue" => typeof(ConfigureQueueCommand),
            "interrupt" => typeof(InterruptCommand),
            "terminate-session" => typeof(TerminateSessionCommand),
            "open-subagent" => typeof(OpenSubagentCommand),
            "modal-response" => typeof(ModalResponseCommand),
            "set-tool-enabled" => typeof(SetToolEnabledCommand),
            "set-continue-in-background" => typeof(SetContinueInBackgroundCommand),
            "detach" => typeof(DetachCommand),
            _ => throw Protocol($"Unknown command discriminator '{type}'."),
        };
        var result = (AgentSessionCommand?)JsonSerializer.Deserialize(element.GetRawText(), target, Options)
            ?? throw Protocol("Command was null.");
        ValidateCommand(result);
        return result;
    }

    internal static JsonElement SerializeFrame(AgentSessionServerFrame frame)
    {
        ValidateFrame(frame);
        _ = AgentSessionProtocolEventCodec.Deserialize(frame);
        return JsonSerializer.SerializeToElement(frame, Options);
    }

    internal static AgentSessionServerFrame DeserializeFrame(JsonElement element)
    {
        RequireOnly(element, FrameNames);
        var type = ReadType(element);
        if (!EventTypes.Contains(type))
            throw Protocol($"Unknown server frame discriminator '{type}'.");
        var frame = new AgentSessionServerFrame
        {
            ProtocolVersion = element.GetProperty("protocol-version").GetInt32(),
            Type = type,
            CorrelationId = element.GetProperty("correlation-id").GetGuid(),
            RuntimeEpoch = JsonSerializer.Deserialize<RuntimeEpoch>(element.GetProperty("runtime-epoch"), Options),
            Sequence = element.GetProperty("sequence").GetInt64(),
            Payload = element.GetProperty("payload").Clone(),
        };
        ValidateFrame(frame);
        _ = AgentSessionProtocolEventCodec.Deserialize(frame);
        return frame;
    }

    private static void ValidateOpen(AgentSessionOpenRequest value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.ProtocolVersion != Version) throw Protocol("Unsupported protocol version.");
        if (!Enum.IsDefined(value.OpenIntent)) throw Protocol("Unknown open intent.");
        RequireText(value.AgentSessionId, nameof(value.AgentSessionId));
        RequireText(value.ExpectedOwningProfileEntityId, nameof(value.ExpectedOwningProfileEntityId));
        RequireText(value.AttachmentToken, nameof(value.AttachmentToken));
        if (!Guid.TryParseExact(value.AttachmentToken, "N", out _))
            throw Protocol("Attachment token must be a 128-bit hexadecimal value.");
        if (value.ExpectedOwnershipGeneration < 0) throw Protocol("Ownership generation cannot be negative.");
        if (value.ReplayCursor is { } cursor && cursor.Epoch.Value == Guid.Empty)
            throw Protocol("Replay cursor epoch cannot be empty.");
        ArgumentNullException.ThrowIfNull(value.Capabilities);
        if (value.Capabilities.Any(string.IsNullOrWhiteSpace)
            || value.Capabilities.Distinct(StringComparer.Ordinal).Count() != value.Capabilities.Count
            || value.Capabilities.Any(c => !KnownCapabilities.Contains(c)))
            throw Protocol("Capabilities contain an unknown, blank, or duplicate value.");
    }

    internal static class AgentSessionProtocolEventCodec
    {
        internal static AgentSessionServerFrame CreateFrame(
            RuntimeEpoch epoch, long sequence, Guid correlationId, AgentSessionServerEvent value)
            => new()
            {
                ProtocolVersion = 1,
                Type = value.Type,
                CorrelationId = correlationId,
                RuntimeEpoch = epoch,
                Sequence = sequence,
                Payload = SerializePayload(value),
            };

        internal static AgentSessionServerEvent Deserialize(AgentSessionServerFrame frame)
        {
            var target = frame.Type switch
            {
                "session-status" => typeof(SessionStatusEvent),
                "session-snapshot" => typeof(SessionSnapshotEvent),
                "history-appended" => typeof(HistoryAppendedEvent),
                "usage-changed" => typeof(UsageChangedEvent),
                "agent-information-changed" => typeof(AgentInformationChangedEvent),
                "queue-changed" => typeof(QueueChangedEvent),
                "streaming-started" => typeof(StreamingStartedEvent),
                "streaming-updated" => typeof(StreamingUpdatedEvent),
                "streaming-completed" => typeof(StreamingCompletedEvent),
                "busy-changed" => typeof(BusyChangedEvent),
                "tools-snapshot" => typeof(ToolsSnapshotEvent),
                "tools-changed" => typeof(ToolsChangedEvent),
                "subagents-snapshot" => typeof(SubagentsSnapshotEvent),
                "subagents-changed" => typeof(SubagentsChangedEvent),
                "modal-raised" => typeof(ModalRaisedEvent),
                "modal-updated" => typeof(ModalUpdatedEvent),
                "modal-dismissed" => typeof(ModalDismissedEvent),
                "session-retention-changed" => typeof(SessionRetentionChangedEvent),
                "command-completed" => typeof(CommandCompletedEvent),
                "operation-error" => typeof(OperationErrorEvent),
                "session-terminal" => typeof(SessionTerminalEvent),
                _ => throw new RemoteAgentProtocolException($"Unknown server frame discriminator '{frame.Type}'."),
            };
            using var document = JsonDocument.Parse(frame.Payload.GetRawText());
            var values = document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            values["type"] = JsonSerializer.SerializeToElement(frame.Type);
            var value = (AgentSessionServerEvent?)JsonSerializer.Deserialize(
                JsonSerializer.Serialize(values, AgentSessionProtocolCodec.Options),
                target,
                AgentSessionProtocolCodec.Options)
                ?? throw new RemoteAgentProtocolException("Server event was null.");
            Validate(value);
            return value;
        }

        private static JsonElement SerializePayload(AgentSessionServerEvent value)
        {
            var serialized = JsonSerializer.SerializeToElement(
                value, value.GetType(), AgentSessionProtocolCodec.Options);
            var payload = serialized.EnumerateObject()
                .Where(p => p.Name != "type")
                .ToDictionary(p => p.Name, p => p.Value.Clone());
            return JsonSerializer.SerializeToElement(payload, AgentSessionProtocolCodec.Options);
        }

        private static void Validate(AgentSessionServerEvent value)
        {
            switch (value)
            {
                case SessionSnapshotEvent snapshot:
                    if (snapshot.Snapshot is null
                        || snapshot.Snapshot.History is null
                        || snapshot.Snapshot.RunningItems is null
                        || snapshot.Snapshot.Tools is null
                        || snapshot.Snapshot.Subagents is null
                        || snapshot.Snapshot.Modals is null)
                        throw new RemoteAgentProtocolException("Session snapshot collections are required.");
                    ProtocolValueValidator.Validate(snapshot.Snapshot.Information);
                    ProtocolValueValidator.Validate(snapshot.Snapshot.Usage);
                    AgentInputQueueSnapshotValidator.Validate(snapshot.Snapshot.InputQueues);
                    if (snapshot.Snapshot.ViewerCount < 0)
                        throw new RemoteAgentProtocolException("Viewer count cannot be negative.");
                    break;
                case UsageChangedEvent usage:
                    ProtocolValueValidator.Validate(usage.Usage);
                    break;
                case AgentInformationChangedEvent information:
                    ProtocolValueValidator.Validate(information.Information);
                    break;
                case QueueChangedEvent queue:
                    if (queue.Revision < 0 || queue.Queues is null || queue.RemovedQueueIds is null
                        || queue.RemovedQueueIds.Any(string.IsNullOrWhiteSpace)
                        || queue.RemovedQueueIds.Distinct(StringComparer.Ordinal).Count() != queue.RemovedQueueIds.Count)
                        throw new RemoteAgentProtocolException("Queue delta is invalid.");
                    AgentInputQueueSnapshotValidator.Validate(new AgentInputQueuesSnapshot
                    {
                        Revision = queue.Revision,
                        Queues = queue.Queues.ToImmutableArray(),
                    });
                    break;
                case SessionRetentionChangedEvent retention when retention.ViewerCount < 0:
                    throw new RemoteAgentProtocolException("Viewer count cannot be negative.");
                case ToolsSnapshotEvent toolSnapshot when toolSnapshot.Tools is null:
                case ToolsChangedEvent toolChange when toolChange.Tools is null:
                    throw new RemoteAgentProtocolException("Tool state is required.");
                case SubagentsSnapshotEvent subagentSnapshot when subagentSnapshot.Subagents is null:
                case SubagentsChangedEvent subagentChange when subagentChange.Subagents is null:
                    throw new RemoteAgentProtocolException("Subagent state is required.");
                case ModalRaisedEvent raisedModal when raisedModal.Modal is null:
                case ModalUpdatedEvent updatedModal when updatedModal.Modal is null:
                    throw new RemoteAgentProtocolException("Modal state is required.");
                case CommandCompletedEvent command when command.CommandId == Guid.Empty:
                    throw new RemoteAgentProtocolException("Completed command id cannot be empty.");
                case OperationErrorEvent operation:
                    Validate(operation.Error);
                    break;
                case HistoryAppendedEvent history when history.Item.ValueKind == JsonValueKind.Undefined:
                    throw new RemoteAgentProtocolException("History item is required.");
                case StreamingStartedEvent streaming:
                    RequireText(streaming.RunId, nameof(streaming.RunId));
                    break;
                case StreamingUpdatedEvent streaming:
                    RequireText(streaming.RunId, nameof(streaming.RunId));
                    break;
                case StreamingCompletedEvent streaming:
                    RequireText(streaming.RunId, nameof(streaming.RunId));
                    break;
                case ModalDismissedEvent modal:
                    RequireText(modal.ModalId, nameof(modal.ModalId));
                    break;
                case SessionTerminalEvent terminal:
                    RequireText(terminal.Reason, nameof(terminal.Reason));
                    break;
            }
        }

        private static void Validate(RemoteAgentOperationError error)
        {
            RequireText(error.Code, nameof(error.Code));
            RequireText(error.Operation, nameof(error.Operation));
            RequireText(error.Message, nameof(error.Message));
            if (error.CorrelationId == Guid.Empty || !AllowedErrorCodes.Contains(error.Code))
                throw new RemoteAgentProtocolException("Remote operation error is invalid.");
        }

        private static readonly HashSet<string> AllowedErrorCodes = new(StringComparer.Ordinal)
        {
            "invalid-request", "unauthorized", "not-found", "owner-mismatch", "generation-mismatch",
            "runtime-changed", "unsupported", "conflict", "rejected", "cancelled", "containment-required",
            "launch-failed", "takeover-blocked", "internal-error",
        };
    }

    private static void ValidateCommand(AgentSessionCommand value)
    {
        if (value.CommandId == Guid.Empty) throw new ArgumentException("Command id cannot be empty.");
        if (value.CorrelationId == Guid.Empty) throw new ArgumentException("Correlation id cannot be empty.");
        if (value.RuntimeEpoch.Value == Guid.Empty) throw Protocol("Runtime epoch cannot be empty.");
        switch (value)
        {
            case CreateQueueCommand command:
                RequireConfiguration(command.Configuration);
                RequireRevision(command.ExpectedRevision);
                break;
            case DeleteQueueCommand command:
                RequireText(command.QueueId, nameof(command.QueueId));
                RequireRevision(command.ExpectedRevision);
                break;
            case EnqueueInputCommand command:
                RequireText(command.TargetQueueId, nameof(command.TargetQueueId));
                RequireRevision(command.ExpectedRevision);
                RequireArray(command.Messages, nameof(command.Messages));
                break;
            case EditQueueItemCommand command:
                RequireText(command.QueueId, nameof(command.QueueId));
                RequireText(command.ItemId, nameof(command.ItemId));
                RequireRevision(command.ExpectedRevision);
                RequireArray(command.Messages, nameof(command.Messages));
                break;
            case RemoveQueueItemCommand command:
                RequireText(command.QueueId, nameof(command.QueueId));
                RequireText(command.ItemId, nameof(command.ItemId));
                RequireRevision(command.ExpectedRevision);
                break;
            case MoveQueueItemCommand command:
                RequireText(command.SourceQueueId, nameof(command.SourceQueueId));
                RequireText(command.ItemId, nameof(command.ItemId));
                RequireText(command.TargetQueueId, nameof(command.TargetQueueId));
                if (command.BeforeItemId is not null) RequireText(command.BeforeItemId, nameof(command.BeforeItemId));
                RequireRevision(command.ExpectedRevision);
                break;
            case ConfigureQueueCommand command:
                RequireText(command.QueueId, nameof(command.QueueId));
                RequireConfiguration(command.Configuration);
                RequireRevision(command.ExpectedRevision);
                break;
            case TerminateSessionCommand command:
                RequireText(command.Reason, nameof(command.Reason));
                break;
            case OpenSubagentCommand command:
                RequireText(command.AgentId, nameof(command.AgentId));
                break;
            case ModalResponseCommand command:
                RequireText(command.ModalId, nameof(command.ModalId));
                if (command.Response.ValueKind == JsonValueKind.Undefined) throw Protocol("Modal response is required.");
                break;
            case SetToolEnabledCommand command:
                RequireText(command.ToolId, nameof(command.ToolId));
                break;
        }
    }

    private static void RequireRevision(long revision)
    {
        if (revision < 0) throw Protocol("Expected revision cannot be negative.");
    }

    private static void RequireConfiguration(AgentInputQueueConfiguration configuration)
    {
        RequireText(configuration.Name, nameof(configuration.Name));
        if (configuration.Priority < 0 || !Enum.IsDefined(configuration.Immediacy))
            throw Protocol("Queue configuration is invalid.");
        if (configuration.CoalescingKey is not null) RequireText(configuration.CoalescingKey, nameof(configuration.CoalescingKey));
    }

    private static void RequireArray(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
            throw Protocol($"{name} must be a nonempty array.");
    }

    private static void ValidateFrame(AgentSessionServerFrame value)
    {
        if (value.ProtocolVersion != Version) throw Protocol("Unsupported protocol version.");
        if (!EventTypes.Contains(value.Type)) throw Protocol("Unknown server frame discriminator.");
        if (value.CorrelationId == Guid.Empty) throw Protocol("Correlation id cannot be empty.");
        if (value.RuntimeEpoch.Value == Guid.Empty) throw Protocol("Runtime epoch cannot be empty.");
        if (value.Sequence <= 0) throw Protocol("Server frame sequence must be positive.");
        if (value.Payload.ValueKind != JsonValueKind.Object) throw Protocol("Server frame payload must be an object.");
    }

    private static string ReadType(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("type", out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw Protocol("A nonblank type discriminator is required.");
        return value.GetString()!;
    }

    private static void RequireObjectAndType(JsonElement element, string expected, HashSet<string> allowed)
    {
        RequireOnly(element, allowed);
        if (!string.Equals(ReadType(element), expected, StringComparison.Ordinal))
            throw Protocol($"Expected '{expected}'.");
    }

    private static void RequireOnly(JsonElement element, HashSet<string> allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Protocol("A JSON object is required.");
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name)) throw Protocol($"Unknown member '{property.Name}'.");
    }

    private static void RequireText(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} must be nonblank.", name);
    }

    private static RemoteAgentProtocolException Protocol(string message) => new(message);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(Microsoft.Extensions.AI.AIJsonUtilities.DefaultOptions)
        {
            PropertyNamingPolicy = JsonNamingPolicy.KebabCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.KebabCaseLower,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Insert(0, new AgentChatModalContentConverter());
        options.Converters.Insert(0, new AgentInformationConverter());
        options.Converters.Insert(0, new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }

    private static readonly HashSet<string> OpenNames = new(StringComparer.Ordinal)
    {
        "type", "protocol-version", "agent-session-id", "expected-owning-profile-entity-id",
        "expected-ownership-generation", "open-intent", "attachment-token", "replay-cursor", "capabilities",
    };
    private static readonly HashSet<string> FrameNames = new(StringComparer.Ordinal)
    {
        "protocol-version", "type", "correlation-id", "runtime-epoch", "sequence", "payload",
    };
    private static readonly HashSet<string> EventTypes = new(StringComparer.Ordinal)
    {
        "session-status", "session-snapshot", "history-appended", "usage-changed",
        "agent-information-changed", "queue-changed", "streaming-started", "streaming-updated",
        "streaming-completed", "busy-changed", "tools-snapshot", "tools-changed",
        "subagents-snapshot", "subagents-changed", "modal-raised", "modal-updated",
        "modal-dismissed", "session-retention-changed", "command-completed", "operation-error",
        "session-terminal",
    };

    private sealed class AgentInformationConverter : JsonConverter<AgentInformation>
    {
        public override AgentInformation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            RequireOnly(root, new HashSet<string>(StringComparer.Ordinal)
            {
                "agent-session-id", "agent-id", "name", "display-name", "description",
                "accepts-user-input", "current-model-id", "agent-definition",
            });
            var value = new AgentInformation
            {
                AgentSessionId = root.GetProperty("agent-session-id").GetString()!,
                AgentId = root.GetProperty("agent-id").GetString()!,
                Name = root.GetProperty("name").GetString()!,
                DisplayName = root.GetProperty("display-name").GetString()!,
                Description = root.GetProperty("description").GetString()!,
                AcceptsUserInput = root.GetProperty("accepts-user-input").GetBoolean(),
                CurrentModelId = root.TryGetProperty("current-model-id", out var model) && model.ValueKind != JsonValueKind.Null
                    ? model.GetString() : null,
                AgentDefinition = PhantomAgentSchema.AgentDefinitionFromJson(
                    root.GetProperty("agent-definition").GetRawText()),
            };
            ProtocolValueValidator.Validate(value);
            return value;
        }

        public override void Write(Utf8JsonWriter writer, AgentInformation value, JsonSerializerOptions options)
        {
            ProtocolValueValidator.Validate(value);
            writer.WriteStartObject();
            writer.WriteString("agent-session-id", value.AgentSessionId);
            writer.WriteString("agent-id", value.AgentId);
            writer.WriteString("name", value.Name);
            writer.WriteString("display-name", value.DisplayName);
            writer.WriteString("description", value.Description);
            writer.WriteBoolean("accepts-user-input", value.AcceptsUserInput);
            if (value.CurrentModelId is null) writer.WriteNull("current-model-id");
            else writer.WriteString("current-model-id", value.CurrentModelId);
            writer.WritePropertyName("agent-definition");
            writer.WriteRawValue(value.AgentDefinition.ToJson());
            writer.WriteEndObject();
        }
    }

    private sealed class AgentChatModalContentConverter : JsonConverter<AgentChatModalContent>
    {
        public override AgentChatModalContent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            var type = ReadType(root);
            return type switch
            {
                "freeform" => JsonSerializer.Deserialize<FreeformModalContent>(root, options)!,
                "multiple-choice" => JsonSerializer.Deserialize<MultipleChoiceModalContent>(root, options)!,
                "approval" => JsonSerializer.Deserialize<ApprovalModalContent>(root, options)!,
                _ => throw Protocol($"Unknown modal content discriminator '{type}'."),
            };
        }

        public override void Write(Utf8JsonWriter writer, AgentChatModalContent value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}

internal sealed class AgentSessionFramePublisher(
    IMessageChannel channel,
    RuntimeEpoch runtimeEpoch)
{
    private readonly SemaphoreSlim publishGate = new(1, 1);
    private long sequence;

    internal async Task<AgentSessionServerFrame> PublishAsync(
        AgentSessionServerEvent value,
        Guid correlationId,
        CancellationToken ct = default)
    {
        await this.publishGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var nextSequence = this.sequence + 1;
            var frame = AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.CreateFrame(
                runtimeEpoch, nextSequence, correlationId, value);
            await channel.Writer.WriteAsync(
                AgentSessionProtocolCodec.SerializeFrame(frame), ct).ConfigureAwait(false);
            this.sequence = nextSequence;
            return frame;
        }
        finally
        {
            this.publishGate.Release();
        }
    }
}

internal static class AgentSessionViewerReleasePolicy
{
    internal static bool ShouldTerminateRuntime(bool continueInBackground, int remainingViewerCount)
    {
        if (remainingViewerCount < 0)
            throw new ArgumentOutOfRangeException(nameof(remainingViewerCount));
        return remainingViewerCount == 0 && !continueInBackground;
    }
}

public sealed class RemoteAgentProtocolException : Exception
{
    public RemoteAgentProtocolException(string message) : base(message) { }
    public RemoteAgentProtocolException(string message, Exception innerException) : base(message, innerException) { }
}
