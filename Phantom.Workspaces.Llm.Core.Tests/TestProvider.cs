using System.Collections.Immutable;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class TestProvider : ILlmProvider
{
    private readonly ImmutableArray<LlmStreamEvent> streamEvents;

    public TestProvider(
        params LlmStreamEvent[] streamEvents)
    {
        this.streamEvents = streamEvents.ToImmutableArray();
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmConversation conversation,
        CancellationToken cancellationToken = default)
    {
        foreach (var streamEvent in this.streamEvents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
            await Task.Yield();
        }
    }

    public static LlmEvent UserTurn(string content)
    {
        return new LlmEvent
        {
            EventKind = LlmEventKinds.Turn,
            Role = LlmRoles.User,
            Content = content,
        };
    }

    public static LlmEvent SystemTurn(string content)
    {
        return new LlmEvent
        {
            EventKind = LlmEventKinds.Turn,
            Role = LlmRoles.System,
            Content = content,
        };
    }

    public static LlmEvent AssistantTurn(
        string? content = null,
        string? thinking = null)
    {
        return new LlmEvent
        {
            EventKind = LlmEventKinds.Turn,
            Role = LlmRoles.Assistant,
            Content = content,
            Thinking = thinking,
        };
    }

    public static LlmEvent AssistantContentToken(string content)
    {
        return new LlmEvent
        {
            EventKind = LlmEventKinds.Token,
            Role = LlmRoles.Assistant,
            Content = content,
        };
    }

    public static LlmEvent AssistantThinkingToken(string thinking)
    {
        return new LlmEvent
        {
            EventKind = LlmEventKinds.Token,
            Role = LlmRoles.Assistant,
            Thinking = thinking,
        };
    }

    public static LlmStreamEvent Content(string content)
    {
        return new LlmStreamEvent
        {
            Event = AssistantTurn(content),
        };
    }

    public static LlmStreamEvent ContentToken(string token)
    {
        return new LlmStreamEvent
        {
            Event = AssistantContentToken(token),
        };
    }

    public static LlmStreamEvent ThinkingToken(string token)
    {
        return new LlmStreamEvent
        {
            Event = AssistantThinkingToken(token),
        };
    }

    public static LlmStreamEvent ToolUse(
        string toolName,
        string arguments)
    {
        return new LlmStreamEvent
        {
            Event = new LlmEvent
            {
                EventKind = LlmEventKinds.ToolCall,
                Role = LlmRoles.Assistant,
                ToolCalls =
                [
                    new LlmEvent
                    {
                        EventKind = LlmEventKinds.ToolCall,
                        ToolName = toolName,
                        Content = arguments,
                    },
                ],
            },
        };
    }

    public static LlmStreamEvent Checkpoint(LlmConversation conversation)
    {
        return new LlmStreamEvent
        {
            Checkpoint = new LlmCheckpointEvent
            {
                Conversation = conversation,
            },
        };
    }
}
