using System.Runtime.CompilerServices;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Llm.Echo;

/// <summary>
/// Small deterministic provider for tests and play exploration.
/// </summary>
public sealed class EchoLlmProvider : ILlmProvider
{
    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmConversation conversation,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var requestedOutput = GetRequestedOutput(conversation);

        if (requestedOutput.StartsWith("tool_use: ", StringComparison.Ordinal))
        {
            yield return CreateToolUseEvent(requestedOutput["tool_use: ".Length..]);
            yield break;
        }

        if (requestedOutput.StartsWith("content-tokens: ", StringComparison.Ordinal))
        {
            foreach (var token in requestedOutput["content-tokens: ".Length..])
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new LlmStreamEvent
                {
                    Event = new LlmEvent
                    {
                        EventKind = LlmEventKinds.Token,
                        Role = LlmRoles.Assistant,
                        Content = token.ToString(),
                    },
                };
                await Task.Yield();
            }

            yield break;
        }

        if (requestedOutput.StartsWith("thinking-tokens: ", StringComparison.Ordinal))
        {
            foreach (var token in requestedOutput["thinking-tokens: ".Length..])
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new LlmStreamEvent
                {
                    Event = new LlmEvent
                    {
                        EventKind = LlmEventKinds.Token,
                        Role = LlmRoles.Assistant,
                        Thinking = token.ToString(),
                    },
                };
                await Task.Yield();
            }

            yield break;
        }

        yield return new LlmStreamEvent
        {
            Event = new LlmEvent
            {
                EventKind = LlmEventKinds.Turn,
                Role = LlmRoles.Assistant,
                Content = requestedOutput,
            },
        };
    }

    public string GetResponse(string requestedOutput)
    {
        return requestedOutput;
    }

    private static string GetRequestedOutput(LlmConversation conversation)
    {
        var lastEvent = conversation.Events.LastOrDefault();
        return lastEvent?.Content ?? string.Empty;
    }

    private static LlmStreamEvent CreateToolUseEvent(string toolUseText)
    {
        var separatorIndex = toolUseText.IndexOf(' ');
        var toolName = separatorIndex >= 0 ? toolUseText[..separatorIndex] : toolUseText;
        var arguments = separatorIndex >= 0 ? toolUseText[(separatorIndex + 1)..] : string.Empty;

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
}
