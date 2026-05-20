using System.Runtime.CompilerServices;

namespace Phantom.Workspaces.Llm;

public sealed class ProjectorLlmProvider : ILlmProvider
{
    private readonly ILlmProvider underlyingProvider;

    public ProjectorLlmProvider(
        ILlmProvider underlyingProvider)
    {
        this.underlyingProvider = underlyingProvider;
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmConversation conversation,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        LlmEvent? pending = null;

        await foreach (var streamEvent in this.underlyingProvider
                           .StreamAsync(conversation, cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            if (streamEvent.Checkpoint is not null || streamEvent.Replace is not null)
            {
                pending = null;
                yield return streamEvent;
                continue;
            }

            if (streamEvent.Event is null)
            {
                pending = null;
                yield return streamEvent;
                continue;
            }

            if (pending is not null
                && TryMerge(pending, streamEvent.Event, out var merged))
            {
                pending = merged;
                yield return new LlmStreamEvent
                {
                    Replace = new LlmReplaceEvent
                    {
                        RemoveCount = 1,
                        Events =
                        [
                            merged,
                        ],
                    },
                };
                continue;
            }

            pending = streamEvent.Event;
            yield return streamEvent;
        }
    }

    private static bool TryMerge(
        LlmEvent previous,
        LlmEvent current,
        out LlmEvent merged)
    {
        if (!CanCoalesce(previous) || !CanCoalesce(current))
        {
            merged = current;
            return false;
        }

        if (!string.Equals(previous.EventKind, current.EventKind, StringComparison.Ordinal)
            || !string.Equals(previous.Role, current.Role, StringComparison.Ordinal))
        {
            merged = current;
            return false;
        }

        merged = new LlmEvent
        {
            Timestamp = current.Timestamp,
            EventKind = current.EventKind,
            Role = current.Role,
            Content = Concat(previous.Content, current.Content),
            ExternalContent = current.ExternalContent ?? previous.ExternalContent,
            ExternalContentName = current.ExternalContentName ?? previous.ExternalContentName,
            Thinking = Concat(previous.Thinking, current.Thinking),
            ToolCalls = current.ToolCalls ?? previous.ToolCalls,
            ToolName = current.ToolName ?? previous.ToolName,
            CorrelationId = current.CorrelationId ?? previous.CorrelationId,
            Done = current.Done ?? previous.Done,
            DoneReason = current.DoneReason ?? previous.DoneReason,
        };

        return true;
    }

    private static bool CanCoalesce(
        LlmEvent streamEvent)
    {
        return string.Equals(streamEvent.EventKind, LlmEventKinds.Turn, StringComparison.Ordinal)
               && string.Equals(streamEvent.Role, LlmRoles.Assistant, StringComparison.Ordinal);
    }

    private static string? Concat(
        string? left,
        string? right)
    {
        if (string.IsNullOrEmpty(left))
        {
            return right;
        }

        if (string.IsNullOrEmpty(right))
        {
            return left;
        }

        return string.Concat(left, right);
    }
}
