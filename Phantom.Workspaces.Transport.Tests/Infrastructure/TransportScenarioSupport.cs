using System.Text.Json;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Transport.Tests.Infrastructure;

/// <summary>
/// Shared helpers for the transport integration scenarios. All helpers are hermetic (no ambient
/// network) and deterministic: executor-side chat behaviour is driven by
/// <see cref="DeterministicTestChatClient"/>, the same deterministic streaming stand-in that
/// <c>ScriptedByokChatServer</c> itself delegates to.
/// </summary>
internal static class TransportScenarioSupport
{
    public static CancellationToken TestToken()
        => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    public static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static JsonElement ChatClientRequest() => Json("""{"type":"chat-client"}""");

    /// <summary>
    /// Builds a <see cref="DeterministicTestChatClient"/> pre-scripted to stream the supplied
    /// assistant text fragments as a single turn and then complete.
    /// </summary>
    public static DeterministicTestChatClient StreamingChatClient(params string[] fragments)
    {
        var client = new DeterministicTestChatClient();
        var stream = client.EnqueueStreamingResponse();
        foreach (var fragment in fragments)
        {
            stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, fragment));
        }

        stream.Complete();
        return client;
    }

    /// <summary>Drives a full streaming turn and returns the concatenated assistant text.</summary>
    public static async Task<string> RunTurnAsync(
        IChatClient client,
        string userText,
        CancellationToken ct)
    {
        var text = string.Empty;
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, userText)], null, ct).ConfigureAwait(false))
        {
            text += update.Text;
        }

        return text;
    }
}
