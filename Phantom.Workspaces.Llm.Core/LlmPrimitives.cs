using System.Collections.Immutable;

namespace Phantom.Workspaces.Llm;

public static class LlmRoles
{
    public const string System = "system";
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string Tool = "tool";
}

public static class LlmEventKinds
{
    public const string Token = "token";
    public const string Turn = "turn";
    public const string ToolCall = "tool_call";
    public const string ToolResult = "tool_result";
    public const string McpNotification = "mcp_notification";
}

public sealed class LlmEvent : IEquatable<LlmEvent>
{
    public DateTimeOffset StartTime { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset EndTime { get; init; } = DateTimeOffset.UtcNow;

    public string? Model { get; init; }

    public required string EventKind { get; init; }

    public string? Role { get; init; }

    public string? Content { get; init; }

    public string? ExternalContent { get; init; }

    public string? ExternalContentName { get; init; }

    public string? Thinking { get; init; }

    public ImmutableList<LlmEvent>? ToolCalls { get; init; }

    public string? ToolName { get; init; }

    public string? CorrelationId { get; init; }

    public bool? Done { get; init; }

    public string? DoneReason { get; init; }

    public static bool operator ==(LlmEvent? left, LlmEvent? right)
    {
        return EqualityComparer<LlmEvent>.Default.Equals(left, right);
    }

    public static bool operator !=(LlmEvent? left, LlmEvent? right)
    {
        return !(left == right);
    }

    public bool Equals(LlmEvent? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null)
        {
            return false;
        }

        return string.Equals(this.EventKind, other.EventKind, StringComparison.Ordinal)
               && string.Equals(this.Model, other.Model, StringComparison.Ordinal)
               && string.Equals(this.Role, other.Role, StringComparison.Ordinal)
               && string.Equals(this.Content, other.Content, StringComparison.Ordinal)
               && string.Equals(this.ExternalContent, other.ExternalContent, StringComparison.Ordinal)
               && string.Equals(this.ExternalContentName, other.ExternalContentName, StringComparison.Ordinal)
               && string.Equals(this.Thinking, other.Thinking, StringComparison.Ordinal)
               && string.Equals(this.ToolName, other.ToolName, StringComparison.Ordinal)
               && string.Equals(this.CorrelationId, other.CorrelationId, StringComparison.Ordinal)
               && this.Done == other.Done
               && string.Equals(this.DoneReason, other.DoneReason, StringComparison.Ordinal)
               && ToolCallsEqual(this.ToolCalls, other.ToolCalls);
    }

    public override bool Equals(object? obj)
    {
        return obj is LlmEvent other && this.Equals(other);
    }

    public override int GetHashCode()
    {
        var hashCode = new HashCode();
        hashCode.Add(this.EventKind, StringComparer.Ordinal);
        hashCode.Add(this.Model, StringComparer.Ordinal);
        hashCode.Add(this.Role, StringComparer.Ordinal);
        hashCode.Add(this.Content, StringComparer.Ordinal);
        hashCode.Add(this.ExternalContent, StringComparer.Ordinal);
        hashCode.Add(this.ExternalContentName, StringComparer.Ordinal);
        hashCode.Add(this.Thinking, StringComparer.Ordinal);
        hashCode.Add(this.ToolName, StringComparer.Ordinal);
        hashCode.Add(this.CorrelationId, StringComparer.Ordinal);
        hashCode.Add(this.Done);
        hashCode.Add(this.DoneReason, StringComparer.Ordinal);

        if (this.ToolCalls is not null)
        {
            foreach (var toolCall in this.ToolCalls)
            {
                hashCode.Add(toolCall);
            }
        }

        return hashCode.ToHashCode();
    }

    private static bool ToolCallsEqual(
        ImmutableList<LlmEvent>? left,
        ImmutableList<LlmEvent>? right)
    {
        return left is null && right is null
               || left is not null
               && right is not null
               && left.SequenceEqual(right);
    }
}

public sealed class LlmReplaceEvent
{
    public required int RemoveCount { get; init; }

    public required ImmutableList<LlmEvent> Events { get; init; }
}

public sealed class LlmCheckpointEvent
{
    public required LlmConversation Conversation { get; init; }
}

public sealed class LlmStreamEvent
{
    public LlmEvent? Event { get; init; }

    public LlmReplaceEvent? Replace { get; init; }

    public LlmCheckpointEvent? Checkpoint { get; init; }
}
