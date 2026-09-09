using System.Collections.Immutable;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Common owner-authoritative queue aggregate exposed by an <see cref="IAgentChat"/> to UI/protocol
/// consumers (issue #1485). Every mutation is a request/reply command referencing stable ids and
/// the aggregate revision; results and snapshots are immutable value types that never carry a live
/// owner reference across transports.
/// </summary>
public interface IAgentInputQueues
{
    AgentInputQueuesSnapshot Snapshot { get; }
    IReadOnlyList<IAgentInputQueue> Queues { get; }
    IAgentInputQueue DefaultQueue { get; }
    IAgentInputQueue ImmediateQueue { get; }
    event EventHandler? Changed;

    Task<AgentInputQueueCommandResult> CreateQueueAsync(
        CreateAgentInputQueueRequest request, CancellationToken ct = default);
    Task<AgentInputQueueCommandResult> DeleteQueueAsync(
        DeleteAgentInputQueueRequest request, CancellationToken ct = default);
    Task<AgentInputQueueCommandResult> EnqueueAsync(
        EnqueueAgentInputRequest request, CancellationToken ct = default);
    Task<AgentInputQueueCommandResult> EditAsync(
        EditAgentInputQueueItemRequest request, CancellationToken ct = default);
    Task<AgentInputQueueCommandResult> RemoveAsync(
        RemoveAgentInputQueueItemRequest request, CancellationToken ct = default);
    Task<AgentInputQueueCommandResult> MoveAsync(
        MoveAgentInputQueueItemRequest request, CancellationToken ct = default);
    Task<AgentInputQueueCommandResult> ConfigureAsync(
        ConfigureAgentInputQueueRequest request, CancellationToken ct = default);

    // #1485 retry 5: synchronous owner-side command surface. Owner-side implementations
    // complete these entirely on the caller's thread (no I/O, no continuation, no waiting)
    // so UI callers can obtain the AgentInputQueueCommandResult without any of the flagged
    // blocking patterns (.Result, .Wait(), .GetAwaiter().GetResult()).
    AgentInputQueueCommandResult CreateQueue(CreateAgentInputQueueRequest request);
    AgentInputQueueCommandResult DeleteQueue(DeleteAgentInputQueueRequest request);
    AgentInputQueueCommandResult Enqueue(EnqueueAgentInputRequest request);
    AgentInputQueueCommandResult Edit(EditAgentInputQueueItemRequest request);
    AgentInputQueueCommandResult Remove(RemoveAgentInputQueueItemRequest request);
    AgentInputQueueCommandResult Move(MoveAgentInputQueueItemRequest request);
    AgentInputQueueCommandResult Configure(ConfigureAgentInputQueueRequest request);
}

/// <summary>Per-queue read model. Snapshots replace atomically on the foreground scheduler.</summary>
public interface IAgentInputQueue
{
    AgentInputQueueSnapshot Snapshot { get; }
    event EventHandler? Changed;
}

public readonly record struct AgentInputQueuesSnapshot
{
    public required long Revision { get; init; }
    public required ImmutableArray<AgentInputQueueSnapshot> Queues { get; init; }
}

public readonly record struct AgentInputQueueSnapshot
{
    public AgentInputQueueSnapshot() { }
    public required string QueueId { get; init; }
    public required string Name { get; init; }
    public required bool IsDefault { get; init; }
    public required bool IsImmediate { get; init; }
    public required AgentInputQueueImmediacy Immediacy { get; init; }
    public required int Priority { get; init; }
    public string? CoalescingKey { get; init; } = null;
    public required long Revision { get; init; }
    public required ImmutableArray<AgentInputItemSnapshot> Items { get; init; }
}

public readonly record struct AgentInputItemSnapshot
{
    public required string ItemId { get; init; }
    public required ImmutableArray<ChatMessage> Messages { get; init; }
}

public readonly record struct AgentInputQueueConfiguration
{
    public AgentInputQueueConfiguration() { }
    public required string Name { get; init; }
    public required AgentInputQueueImmediacy Immediacy { get; init; }
    public required int Priority { get; init; }
    public string? CoalescingKey { get; init; } = null;
}

public enum AgentInputQueueCommandStatus
{
    Applied,
    Duplicate,
    Conflict,
    Rejected,
}

public readonly record struct AgentInputQueueCommandResult
{
    public AgentInputQueueCommandResult() { }
    public required Guid CommandId { get; init; }
    public required AgentInputQueueCommandStatus Status { get; init; }
    public string? QueueId { get; init; } = null;
    public string? ItemId { get; init; } = null;
    public required long Revision { get; init; }
    public string? ErrorCode { get; init; } = null;
    public AgentInputQueuesSnapshot? CurrentSnapshot { get; init; } = null;
}

public sealed record CreateAgentInputQueueRequest
{
    public required AgentInputQueueConfiguration Configuration { get; init; }
    public required Guid CommandId { get; init; }
    public required long ExpectedRevision { get; init; }
}

public sealed record DeleteAgentInputQueueRequest
{
    public required string QueueId { get; init; }
    public required Guid CommandId { get; init; }
    public required long ExpectedRevision { get; init; }
}

public sealed record EnqueueAgentInputRequest
{
    public required string TargetQueueId { get; init; }
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    public required Guid CommandId { get; init; }
    public required long ExpectedRevision { get; init; }
}

public sealed record EditAgentInputQueueItemRequest
{
    public required string QueueId { get; init; }
    public required string ItemId { get; init; }
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    public required Guid CommandId { get; init; }
    public required long ExpectedRevision { get; init; }
}

public sealed record RemoveAgentInputQueueItemRequest
{
    public required string QueueId { get; init; }
    public required string ItemId { get; init; }
    public required Guid CommandId { get; init; }
    public required long ExpectedRevision { get; init; }
}

public sealed record MoveAgentInputQueueItemRequest
{
    public required string SourceQueueId { get; init; }
    public required string ItemId { get; init; }
    public required string TargetQueueId { get; init; }
    public string? BeforeItemId { get; init; } = null;
    public required Guid CommandId { get; init; }
    public required long ExpectedRevision { get; init; }
}

public sealed record ConfigureAgentInputQueueRequest
{
    public required string QueueId { get; init; }
    public required AgentInputQueueConfiguration Configuration { get; init; }
    public required Guid CommandId { get; init; }
    public required long ExpectedRevision { get; init; }
}

/// <summary>Stable safe error codes returned in <see cref="AgentInputQueueCommandResult.ErrorCode"/>.</summary>
public static class AgentInputQueueErrorCodes
{
    public const string InvalidRequest = "invalid-request";
    public const string UnknownQueue = "unknown-queue";
    public const string UnknownItem = "unknown-item";
    public const string DuplicateName = "duplicate-name";
    public const string FixedRoleConfiguration = "fixed-role-configuration";
    public const string ProtectedQueue = "protected-queue";
    public const string QueueNotEmpty = "queue-not-empty";
    public const string ItemAlreadyConsumed = "item-already-consumed";
}
