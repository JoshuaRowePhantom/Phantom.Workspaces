using System.Text;
using Phantom.Workspaces.Llm.Provider.Llama;

namespace Phantom.Workspaces.Llm.Provider.Llama.Tests;

public sealed class OllamaStreamLlmProviderTests
{
    [Fact]
    public async Task StreamAsync_ParsesThinkingChunksFromChatTranscript()
    {
        using var stream = CreateTranscriptStream(
            """< {"model":"qwen3.6","created_at":"2026-05-19T22:14:40.6206026Z","message":{"role":"assistant","content":"","thinking":"The"},"done":false}""",
            """< {"model":"qwen3.6","created_at":"2026-05-19T22:14:40.7978247Z","message":{"role":"assistant","content":"","thinking":" user"},"done":false}""");
        var provider = new OllamaStreamLlmProvider(stream);

        var events = await ReadAllAsync(provider);

        Assert.Equal(2, events.Count);
        Assert.All(events, streamEvent => Assert.Equal(LlmEventKinds.Turn, streamEvent.Event!.EventKind));
        Assert.Equal("The", events[0].Event!.Thinking);
        Assert.Equal(" user", events[1].Event!.Thinking);
        Assert.All(events, streamEvent => Assert.Equal("qwen3.6", streamEvent.Event!.Model));
        Assert.Equal(DateTimeOffset.Parse("2026-05-19T22:14:40.6206026Z"), events[0].Event!.StartTime);
        Assert.Equal(events[0].Event!.StartTime, events[0].Event!.EndTime);
    }

    [Fact]
    public async Task StreamAsync_ParsesToolCallChunkFromChatTranscript()
    {
        using var stream = CreateTranscriptStream(
            """< {"model":"qwen3.6","created_at":"2026-05-19T22:14:59.2756348Z","message":{"role":"assistant","content":"","tool_calls":[{"id":"call_3w2c9kf4","function":{"index":0,"name":"execute_command","arguments":{"command":"dotnet build Phantom.Workspaces.slnx"}}}]},"done":false}""");
        var provider = new OllamaStreamLlmProvider(stream);

        var events = await ReadAllAsync(provider);

        Assert.Single(events);
        var toolCallEvent = events[0].Event!;
        var toolCall = Assert.Single(toolCallEvent.ToolCalls!);
        Assert.Equal(LlmEventKinds.ToolCall, toolCallEvent.EventKind);
        Assert.Equal(LlmRoles.Assistant, toolCallEvent.Role);
        Assert.Equal("execute_command", toolCall.ToolName);
        Assert.Equal("call_3w2c9kf4", toolCall.CorrelationId);
        Assert.Equal("""{"command":"dotnet build Phantom.Workspaces.slnx"}""", toolCall.Content);
        Assert.Equal("qwen3.6", toolCallEvent.Model);
        Assert.Equal(DateTimeOffset.Parse("2026-05-19T22:14:59.2756348Z"), toolCallEvent.StartTime);
        Assert.Equal(toolCallEvent.StartTime, toolCallEvent.EndTime);
    }

    [Fact]
    public async Task StreamAsync_ParsesGenerateResponseChunk()
    {
        using var stream = CreateTranscriptStream(
            """{"model":"qwen3.6","created_at":"2026-05-19T23:46:55.9906659Z","response":"It","done":true,"done_reason":"length"}""");
        var provider = new OllamaStreamLlmProvider(stream);

        var events = await ReadAllAsync(provider);

        Assert.Single(events);
        Assert.Equal(LlmEventKinds.Turn, events[0].Event!.EventKind);
        Assert.Equal(LlmRoles.Assistant, events[0].Event!.Role);
        Assert.Equal("It", events[0].Event!.Content);
        Assert.Equal("qwen3.6", events[0].Event!.Model);
        Assert.Equal(DateTimeOffset.Parse("2026-05-19T23:46:55.9906659Z"), events[0].Event!.StartTime);
        Assert.Equal(events[0].Event!.StartTime, events[0].Event!.EndTime);
    }

    private static async Task<List<LlmStreamEvent>> ReadAllAsync(
        OllamaStreamLlmProvider provider)
    {
        var events = new List<LlmStreamEvent>();
        await foreach (var streamEvent in provider.StreamAsync(LlmConversation.Create()))
        {
            events.Add(streamEvent);
        }

        return events;
    }

    private static MemoryStream CreateTranscriptStream(
        params string[] lines)
    {
        var transcript = string.Join(Environment.NewLine, lines) + Environment.NewLine;
        return new MemoryStream(Encoding.UTF8.GetBytes(transcript));
    }
}
