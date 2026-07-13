using System.Net.Http;
using System.Text;
using Xunit;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Deterministic coverage for <see cref="ScriptedByokChatServer"/> (issue #912): request
/// classification into named per-conversation scripts with independent turn counters, awaitable
/// per-step gates, streamed OpenAI <c>tool_calls</c> deltas, and loud failure (never a hang) for
/// unmatched or unscripted requests.
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
        Assert.Contains("No conversation script matched", failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScriptedByokChatServer_ExhaustedConversation_FailsLoudly()
    {
        await using var server = new ScriptedByokChatServer();
        var main = server.AddConversation("main", request => request.AnyMessageContains("user", "marker"));
        main.AddTurn().AddText("only turn");

        using var httpClient = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };
        using var first = await httpClient.PostAsync("v1/chat/completions", ChatRequest("marker one"));
        Assert.Equal(200, (int)first.StatusCode);

        using var second = await httpClient.PostAsync("v1/chat/completions", ChatRequest("marker two"));
        Assert.Equal(500, (int)second.StatusCode);
        var failure = Assert.Single(server.Failures);
        Assert.Contains("no scripted turns left", failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScriptedByokChatServer_MultipleConversations_IsolatedByPromptContent()
    {
        await using var server = new ScriptedByokChatServer();
        var main = server.AddConversation("main", request => request.AnyMessageContains("user", "MAIN"));
        var mainTurn0 = main.AddTurn();
        mainTurn0.AddText("main-reply-0");
        var mainTurn1 = main.AddTurn();
        mainTurn1.AddText("main-reply-1");

        var sub = server.AddConversation("sub", request => request.AnyMessageContains("user", "SUB"));
        var subTurn0 = sub.AddTurn();
        subTurn0.AddText("sub-reply-0");

        using var httpClient = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };

        var firstMain = await ReadSseAsync(httpClient, "MAIN please");
        var firstSub = await ReadSseAsync(httpClient, "SUB please");
        var secondMain = await ReadSseAsync(httpClient, "MAIN again");

        Assert.Contains("main-reply-0", firstMain, StringComparison.Ordinal);
        Assert.Contains("sub-reply-0", firstSub, StringComparison.Ordinal);
        Assert.Contains("main-reply-1", secondMain, StringComparison.Ordinal);

        Assert.Empty(server.Failures);
        Assert.Equal(0, (await mainTurn0.Request).TurnIndex);
        Assert.Equal(1, (await mainTurn1.Request).TurnIndex);
        Assert.Equal(0, (await subTurn0.Request).TurnIndex);
        Assert.Equal("main", (await mainTurn1.Request).Conversation);
        Assert.Equal("sub", (await subTurn0.Request).Conversation);
    }

    [Fact]
    public async Task ScriptedByokChatServer_GatedStep_DoesNotReplyUntilReleased()
    {
        await using var server = new ScriptedByokChatServer();
        var main = server.AddConversation("main", _ => true);
        var turn = main.AddTurn();
        turn.AddText("before-gate ");
        var gate = turn.AddGate();
        turn.AddText("after-gate");

        using var httpClient = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };
        var responseTask = ReadSseAsync(httpClient, "anything");

        // Deterministic partial-order check: the request has been consumed by the scripted turn,
        // yet the response body cannot complete because the gate is still held.
        await turn.Request.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(responseTask.IsCompleted);

        gate.Release();
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
        main.AddTurn().AddToolCall(0, "call_1", "powershell", """{"command":"Write-Output \"x\""}""");

        using var httpClient = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };
        var body = await ReadSseAsync(httpClient, "anything");

        // Captured-exchange derived shape: a header chunk carrying id/type/name with empty
        // arguments, an argument-fragment chunk, then finish_reason=tool_calls.
        Assert.Contains("\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"powershell\",\"arguments\":\"\"}}]", body, StringComparison.Ordinal);
        Assert.Contains("Write-Output", body, StringComparison.Ordinal);
        Assert.Contains("\"finish_reason\":\"tool_calls\"", body, StringComparison.Ordinal);
        Assert.Contains("data: [DONE]", body, StringComparison.Ordinal);
        Assert.Empty(server.Failures);
    }

    /// <summary>
    /// End-to-end wire compatibility: the scripted <c>tool_calls</c> SSE deltas round-trip through
    /// the real Copilot CLI, which executes the scripted <c>powershell</c> tool and posts its
    /// genuine output back as the tool-result message of the follow-up turn. Requires the local
    /// Copilot CLI; carries the WebView category so the default fast suite stays hermetic while
    /// targeted runs (issue #912 validation) exercise it.
    /// </summary>
    [Fact]
    [Trait("Category", "WebView")]
    public async Task ScriptedByokChatServer_StreamsToolCallDeltas_ParsableByCli()
    {
        await using var server = new ScriptedByokChatServer();
        var main = server.AddConversation("main", _ => true);
        var toolTurn = main.AddTurn();
        toolTurn.AddToolCall(
            0,
            "call_ps",
            "powershell",
            """{"command":"Write-Output \"roundtrip-proof\"","description":"Round-trip check"}""");
        var finalTurn = main.AddTurn();
        finalTurn.AddText("cli-roundtrip-complete");
        main.AddTurn().AddText("spare");

        var byok = new CopilotByokOptions
        {
            BaseUrl = server.BaseUrl,
            ApiKey = "test-key",
        };

        await using var chatClient = new CopilotSdkChatClient(
            "gpt-test",
            "roundtrip",
            gitHubToken: null,
            loggerFactory: null,
            byokOptions: byok,
            cliPath: CopilotCliLocator.FindOrThrow());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var response = await chatClient.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "run the scripted tool")],
            cancellationToken: cts.Token);

        Assert.Contains("cli-roundtrip-complete", response.Text, StringComparison.Ordinal);

        // The CLI really executed the scripted powershell call: its output travels back to the
        // model as the tool-result message of the second scripted turn.
        var followUp = await finalTurn.Request.WaitAsync(cts.Token);
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
