using AgentSchema;
using Microsoft.Extensions.AI;
using System.Net.Http;
using System.Text;
using Xunit;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Deterministic coverage for <see cref="ScriptedByokChatServer"/> (issue #912): request
/// classification into named conversations each backed by its own
/// <see cref="DeterministicTestChatClient"/>, gating via the client's readiness mechanism,
/// translation of streamed <see cref="FunctionCallContent"/> into OpenAI <c>tool_calls</c> deltas,
/// and loud failure (never a hang) for unmatched requests or exhausted response queues.
/// </summary>
public sealed class ScriptedByokChatServerTests
{
    private static StringContent ChatRequest(string userText, bool stream = true)
        => new(
            $$"""{"model":"test","stream":{{(stream ? "true" : "false")}},"messages":[{"role":"user","content":{{System.Text.Json.JsonSerializer.Serialize(userText)}}}]}""",
            Encoding.UTF8,
            "application/json");

    [Fact]
    public async Task ScriptedByokChatServer_UnmatchedRequest_FailsLoudly()
    {
        await using var server = new ScriptedByokChatServer();
        server.AddConversation("main", request => request.AnyMessageContains("user", "expected-marker"));

        using var httpClient = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };
        using var response = await httpClient.PostAsync("v1/chat/completions", ChatRequest("something else entirely"));

        Assert.Equal(500, (int)response.StatusCode);
        var failure = Assert.Single(server.Failures);
        Assert.Contains("No conversation matched", failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScriptedByokChatServer_ExhaustedConversation_FailsLoudly()
    {
        await using var server = new ScriptedByokChatServer();
        var main = server.AddConversation("main", request => request.AnyMessageContains("user", "marker"));
        var stream = main.Client.EnqueueStreamingResponse();
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "only turn"));
        stream.Complete();

        using var httpClient = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };
        using var first = await httpClient.PostAsync("v1/chat/completions", ChatRequest("marker one"));
        Assert.Equal(200, (int)first.StatusCode);

        using var second = await httpClient.PostAsync("v1/chat/completions", ChatRequest("marker two"));
        Assert.Equal(500, (int)second.StatusCode);
        var failure = Assert.Single(server.Failures);
        Assert.Contains("no queued streaming responses left", failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScriptedByokChatServer_MultipleConversations_IsolatedByPromptContent()
    {
        await using var server = new ScriptedByokChatServer();
        var main = server.AddConversation("main", request => request.AnyMessageContains("user", "MAIN"));
        var mainStream0 = main.Client.EnqueueStreamingResponse();
        mainStream0.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "main-reply-0"));
        mainStream0.Complete();
        var mainStream1 = main.Client.EnqueueStreamingResponse();
        mainStream1.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "main-reply-1"));
        mainStream1.Complete();

        var sub = server.AddConversation("sub", request => request.AnyMessageContains("user", "SUB"));
        var subStream0 = sub.Client.EnqueueStreamingResponse();
        subStream0.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "sub-reply-0"));
        subStream0.Complete();

        using var httpClient = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };

        var firstMain = await ReadSseAsync(httpClient, "MAIN please");
        var firstSub = await ReadSseAsync(httpClient, "SUB please");
        var secondMain = await ReadSseAsync(httpClient, "MAIN again");

        Assert.Contains("main-reply-0", firstMain, StringComparison.Ordinal);
        Assert.Contains("sub-reply-0", firstSub, StringComparison.Ordinal);
        Assert.Contains("main-reply-1", secondMain, StringComparison.Ordinal);

        Assert.Empty(server.Failures);
        Assert.Equal(0, (await main.GetRequestAsync(0)).TurnIndex);
        Assert.Equal(1, (await main.GetRequestAsync(1)).TurnIndex);
        Assert.Equal(0, (await sub.GetRequestAsync(0)).TurnIndex);
        Assert.Equal("main", (await main.GetRequestAsync(1)).Conversation);
        Assert.Equal("sub", (await sub.GetRequestAsync(0)).Conversation);
    }

    [Fact]
    public async Task ScriptedByokChatServer_GatedUpdate_DoesNotReplyUntilMarkedReady()
    {
        await using var server = new ScriptedByokChatServer();
        var main = server.AddConversation("main", _ => true);
        var stream = main.Client.EnqueueStreamingResponse();
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "before-gate "));
        var gated = stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "after-gate"), isReady: false);
        stream.Complete();

        using var httpClient = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };
        var responseTask = ReadSseAsync(httpClient, "anything");

        // Deterministic partial-order check: the request has been classified into the
        // conversation, yet the response body cannot complete because the second streamed update
        // is not yet marked ready.
        await main.GetRequestAsync(0).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(responseTask.IsCompleted);

        gated.MarkReady();
        var body = await responseTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Contains("before-gate", body, StringComparison.Ordinal);
        Assert.Contains("after-gate", body, StringComparison.Ordinal);
        Assert.Empty(server.Failures);
    }

    [Fact]
    public async Task ScriptedByokChatServer_StreamsToolCallDeltas_WithHeaderAndArgumentFragments()
    {
        await using var server = new ScriptedByokChatServer();
        var main = server.AddConversation("main", _ => true);
        var stream = main.Client.EnqueueStreamingResponse();
        stream.EnqueueUpdate(new ChatResponseUpdate(
            ChatRole.Assistant,
            [new FunctionCallContent("call_1", "lookup_widget", new Dictionary<string, object?> { ["widgetId"] = "w-42" })]));
        stream.Complete();

        using var httpClient = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };
        var body = await ReadSseAsync(httpClient, "anything");

        // Wire shape: a header chunk carrying id/type/name with empty arguments, an
        // argument-fragment chunk, then finish_reason=tool_calls.
        Assert.Contains("\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"lookup_widget\",\"arguments\":\"\"}}]", body, StringComparison.Ordinal);
        Assert.Contains("w-42", body, StringComparison.Ordinal);
        Assert.Contains("\"finish_reason\":\"tool_calls\"", body, StringComparison.Ordinal);
        Assert.Contains("data: [DONE]", body, StringComparison.Ordinal);
        Assert.Empty(server.Failures);
    }

    /// <summary>
    /// End-to-end wire compatibility: the scripted <c>tool_calls</c> SSE deltas round-trip through
    /// the real Copilot CLI, which executes the scripted <c>powershell</c> tool and posts its
    /// genuine output back as the tool-result message of the follow-up turn. The chat client is
    /// resolved from a BYOK agent definition through <see cref="AgentFactory.CreateChatClient"/>,
    /// exactly as production does. Requires the local Copilot CLI; carries the WebView category so
    /// the default fast suite stays hermetic while targeted runs (issue #912 validation) exercise
    /// it.
    /// </summary>
    [Fact]
    [Trait("Category", "WebView")]
    public async Task ScriptedByokChatServer_StreamsToolCallDeltas_ParsableByCli()
    {
        await using var server = new ScriptedByokChatServer();
        var main = server.AddConversation("main", _ => true);
        var toolTurn = main.Client.EnqueueStreamingResponse();
        toolTurn.EnqueueUpdate(new ChatResponseUpdate(
            ChatRole.Assistant,
            [new FunctionCallContent("call_ps", "powershell", new Dictionary<string, object?>
            {
                ["command"] = "Write-Output \"roundtrip-proof\"",
                ["description"] = "Round-trip check",
            })]));
        toolTurn.Complete();
        var finalTurn = main.Client.EnqueueStreamingResponse();
        finalTurn.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "cli-roundtrip-complete"));
        finalTurn.Complete();
        var spareTurn = main.Client.EnqueueStreamingResponse();
        spareTurn.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "spare"));
        spareTurn.Complete();

        var definition = AgentDefinitionLoader.LoadAgentFromJson($$"""
        {
          "kind": "prompt",
          "name": "roundtrip",
          "model": {
            "id": "gpt-test",
            "provider": "openai",
            "connection": {
              "kind": "key",
              "endpoint": "{{server.BaseUrl}}",
              "apiKey": "test-key"
            },
            "options": {
              "additionalProperties": {
                "cliPath": {{System.Text.Json.JsonSerializer.Serialize(CopilotCliLocator.FindOrThrow())}}
              }
            }
          }
        }
        """);

        var result = AgentFactory.CreateChatClient(definition);
        await using var chatClient = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var response = await chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "run the scripted tool")],
            cancellationToken: cts.Token);

        Assert.Contains("cli-roundtrip-complete", response.Text, StringComparison.Ordinal);

        // The CLI really executed the scripted powershell call: its output travels back to the
        // model as the tool-result message of the second scripted turn.
        var followUp = await main.GetRequestAsync(1).WaitAsync(cts.Token);
        Assert.True(
            followUp.AnyMessageContains("tool", "roundtrip-proof"),
            "The follow-up request should carry the genuine powershell output as a tool message.");
        Assert.Empty(server.Failures);
    }

    private static async Task<string> ReadSseAsync(HttpClient httpClient, string userText)
    {
        using var response = await httpClient.PostAsync("v1/chat/completions", ChatRequest(userText));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
