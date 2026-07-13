using Microsoft.Extensions.AI;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// A thin, protocol-generic OpenAI-compatible chat-completions wire adapter over
/// <see cref="DeterministicTestChatClient"/> for full-stack BYOK tests (issue #912). A single
/// endpoint can serve multiple distinct conversations (for example, a main session plus one per
/// sub-agent): the adapter classifies each incoming request by inspecting its message content and
/// delegates it to the matched conversation's own <see cref="DeterministicTestChatClient"/>, then
/// translates the resulting <see cref="ChatResponseUpdate"/> stream (<see cref="TextContent"/>,
/// <see cref="FunctionCallContent"/>) into OpenAI SSE content and <c>tool_calls</c> deltas. All
/// scripting — responses, streamed deltas, and readiness gating — is expressed through
/// <see cref="DeterministicTestChatClient"/>'s queue and readiness mechanisms; this class carries
/// no knowledge of any particular consumer's tools or prompts. Requests that match no
/// conversation, or that arrive after a conversation's queued responses are exhausted, fail
/// loudly: the adapter responds 500 and records a diagnostic in <see cref="Failures"/> (it never
/// hangs).
/// </summary>
public sealed class ScriptedByokChatServer : IAsyncDisposable
{
    private readonly HttpListener listener;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task acceptLoop;
    private readonly List<ConversationClient> conversations = [];
    private readonly object conversationsLock = new();
    private readonly ConcurrentQueue<string> failures = new();
    private readonly ConcurrentQueue<CapturedRequest> recordedRequests = new();

    /// <summary>Starts the server on a free loopback port.</summary>
    public ScriptedByokChatServer()
    {
        var port = GetFreePort();
        this.BaseUrl = $"http://localhost:{port}/";
        this.listener = new HttpListener();
        this.listener.Prefixes.Add(this.BaseUrl);
        this.listener.Start();
        this.acceptLoop = Task.Run(() => this.AcceptLoopAsync(this.cancellation.Token));
    }

    /// <summary>The absolute base URL the server is listening on.</summary>
    public string BaseUrl { get; }

    /// <summary>Diagnostics recorded for unmatched or unscripted requests. Must be empty at test end.</summary>
    public IReadOnlyCollection<string> Failures => [.. this.failures];

    /// <summary>Every chat-completions request received, in arrival order.</summary>
    public IReadOnlyCollection<CapturedRequest> RecordedRequests => [.. this.recordedRequests];

    /// <summary>Optional sink receiving a line-oriented trace of all server activity.</summary>
    public Action<string>? Trace { get; set; }

    /// <summary>
    /// Registers a named conversation. Incoming requests are classified by evaluating each
    /// conversation's <paramref name="matcher"/> in registration order; the first match wins and
    /// the request is delegated to that conversation's <see cref="ConversationClient.Client"/>.
    /// </summary>
    public ConversationClient AddConversation(string name, Func<CapturedRequest, bool> matcher)
    {
        var conversation = new ConversationClient(name, matcher);
        lock (this.conversationsLock)
        {
            this.conversations.Add(conversation);
        }

        return conversation;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await this.cancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            this.listener.Stop();
            this.listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            await this.acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        this.cancellation.Dispose();
    }

    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await this.listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = Task.Run(() => this.HandleAsync(context), cancellationToken);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            string body;
            using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
            {
                body = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            var path = context.Request.Url?.AbsolutePath ?? string.Empty;
            this.Trace?.Invoke($"REQUEST {context.Request.HttpMethod} {path}\n{body}");

            if (!path.Contains("chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                this.RecordFailure($"Unexpected request path '{path}'. Body: {Truncate(body)}");
                await WriteJsonAsync(context, 404, "{\"error\":\"unexpected path\"}").ConfigureAwait(false);
                return;
            }

            var request = CapturedRequest.Parse(path, body);
            this.recordedRequests.Enqueue(request);

            ConversationClient? conversation = null;
            lock (this.conversationsLock)
            {
                foreach (var candidate in this.conversations)
                {
                    if (candidate.Matches(request))
                    {
                        conversation = candidate;
                        break;
                    }
                }
            }

            if (conversation is null)
            {
                this.RecordFailure($"No conversation matched request. Body: {Truncate(body)}");
                await WriteJsonAsync(context, 500, "{\"error\":\"no conversation matched\"}").ConfigureAwait(false);
                return;
            }

            request.Conversation = conversation.Name;
            request.TurnIndex = conversation.TakeNextTurnIndex();
            this.Trace?.Invoke($"MATCH conversation='{conversation.Name}' turn={request.TurnIndex}");
            conversation.CompleteRequest(request);

            // Fail loudly instead of blocking inside GetStreamingResponseAsync when the
            // conversation's scripted responses are exhausted: the adapter must never hang a test.
            if (conversation.Client.QueuedStreamingResponseCount == 0)
            {
                this.RecordFailure(
                    $"Conversation '{conversation.Name}' has no queued streaming responses left. Body: {Truncate(body)}");
                await WriteJsonAsync(context, 500, "{\"error\":\"conversation responses exhausted\"}").ConfigureAwait(false);
                return;
            }

            await this.WriteSseFromClientAsync(context, conversation, request).ConfigureAwait(false);
            this.Trace?.Invoke($"REPLY-COMPLETE conversation='{conversation.Name}' turn={request.TurnIndex}");
        }
        catch (Exception exception)
        {
            this.RecordFailure($"Request handling threw: {exception}");
            try
            {
                await WriteJsonAsync(
                    context,
                    500,
                    $"{{\"error\":{JsonSerializer.Serialize(exception.Message)}}}").ConfigureAwait(false);
            }
            catch
            {
                // Best effort: the connection may already be gone.
            }
        }
    }

    private void RecordFailure(string message)
    {
        this.failures.Enqueue(message);
        this.Trace?.Invoke($"FAILURE {message}");
    }

    private static string Truncate(string value)
        => value.Length <= 4000 ? value : value[..4000] + "…";

    private async Task WriteSseFromClientAsync(
        HttpListenerContext context,
        ConversationClient conversation,
        CapturedRequest request)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/event-stream";
        context.Response.SendChunked = true;
        var output = context.Response.OutputStream;

        var completionId = $"chatcmpl-{request.TurnIndex}-{Guid.NewGuid():n}";
        var wroteRole = false;
        var toolCallIndex = 0;
        string? finishReason = null;

        await foreach (var update in conversation.Client
            .GetStreamingResponseAsync(request.ToChatMessages(), options: null, this.cancellation.Token)
            .ConfigureAwait(false))
        {
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextContent text:
                        await WriteSseEventAsync(output, BuildChunk(completionId, delta =>
                        {
                            if (!wroteRole)
                            {
                                delta["role"] = "assistant";
                            }

                            delta["content"] = text.Text;
                        })).ConfigureAwait(false);
                        wroteRole = true;
                        break;

                    case FunctionCallContent functionCall:
                        // OpenAI streaming tool_calls: the first delta carries id/type/name,
                        // argument fragments follow with the same index. Emit the header and the
                        // full argument payload as two chunks so the client exercises its
                        // fragment-joining path.
                        var callIndex = toolCallIndex++;
                        var argumentsJson = JsonSerializer.Serialize(
                            functionCall.Arguments ?? new Dictionary<string, object?>());

                        await WriteSseEventAsync(output, BuildChunk(completionId, delta =>
                        {
                            if (!wroteRole)
                            {
                                delta["role"] = "assistant";
                            }

                            delta["tool_calls"] = new JsonArray(new JsonObject
                            {
                                ["index"] = callIndex,
                                ["id"] = functionCall.CallId,
                                ["type"] = "function",
                                ["function"] = new JsonObject
                                {
                                    ["name"] = functionCall.Name,
                                    ["arguments"] = string.Empty,
                                },
                            });
                        })).ConfigureAwait(false);
                        wroteRole = true;

                        await WriteSseEventAsync(output, BuildChunk(completionId, delta =>
                        {
                            delta["tool_calls"] = new JsonArray(new JsonObject
                            {
                                ["index"] = callIndex,
                                ["function"] = new JsonObject
                                {
                                    ["arguments"] = argumentsJson,
                                },
                            });
                        })).ConfigureAwait(false);
                        break;

                    default:
                        throw new InvalidOperationException(
                            $"Unsupported streamed content type '{content.GetType()}'.");
                }
            }

            if (update.FinishReason is { } explicitFinishReason)
            {
                finishReason = explicitFinishReason.Value;
            }
        }

        finishReason ??= toolCallIndex > 0 ? "tool_calls" : "stop";

        var finalChunk = new JsonObject
        {
            ["id"] = completionId,
            ["object"] = "chat.completion.chunk",
            ["created"] = 0,
            ["model"] = "scripted",
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["delta"] = new JsonObject(),
                ["finish_reason"] = finishReason,
            }),
        };

        await WriteSseEventAsync(output, finalChunk.ToJsonString()).ConfigureAwait(false);
        await WriteSseEventAsync(output, "[DONE]").ConfigureAwait(false);
        output.Close();
        context.Response.Close();
    }

    private static string BuildChunk(string completionId, Action<JsonObject> populateDelta)
    {
        var delta = new JsonObject();
        populateDelta(delta);
        var chunk = new JsonObject
        {
            ["id"] = completionId,
            ["object"] = "chat.completion.chunk",
            ["created"] = 0,
            ["model"] = "scripted",
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["delta"] = delta,
                ["finish_reason"] = null,
            }),
        };

        return chunk.ToJsonString();
    }

    private static async Task WriteSseEventAsync(Stream outputStream, string data)
    {
        var bytes = Encoding.UTF8.GetBytes($"data: {data}\n\n");
        await outputStream.WriteAsync(bytes).ConfigureAwait(false);
        await outputStream.FlushAsync().ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, int statusCode, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.OutputStream.Close();
        context.Response.Close();
    }
}

/// <summary>
/// A parsed, recorded chat-completions request: the raw body plus the flattened message list used
/// for conversation classification.
/// </summary>
public sealed class CapturedRequest
{
    private CapturedRequest(string path, string body, IReadOnlyList<(string Role, string Content)> messages)
    {
        this.Path = path;
        this.Body = body;
        this.Messages = messages;
    }

    /// <summary>The request path, for example <c>/v1/chat/completions</c>.</summary>
    public string Path { get; }

    /// <summary>The raw JSON request body.</summary>
    public string Body { get; }

    /// <summary>The flattened (role, text) message list from the request body.</summary>
    public IReadOnlyList<(string Role, string Content)> Messages { get; }

    /// <summary>The conversation name this request was routed to, set after classification.</summary>
    public string? Conversation { get; internal set; }

    /// <summary>The zero-based per-conversation request index, set after classification.</summary>
    public int TurnIndex { get; internal set; } = -1;

    /// <summary>Returns whether any message with the given role contains <paramref name="text"/>.</summary>
    public bool AnyMessageContains(string role, string text)
    {
        foreach (var (messageRole, content) in this.Messages)
        {
            if (string.Equals(messageRole, role, StringComparison.OrdinalIgnoreCase)
                && content.Contains(text, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Converts the flattened wire messages into <see cref="ChatMessage"/>s.</summary>
    internal List<ChatMessage> ToChatMessages()
    {
        var messages = new List<ChatMessage>(this.Messages.Count);
        foreach (var (role, content) in this.Messages)
        {
            messages.Add(new ChatMessage(MapRole(role), content));
        }

        return messages;
    }

    private static ChatRole MapRole(string role) => role.ToLowerInvariant() switch
    {
        "system" => ChatRole.System,
        "assistant" => ChatRole.Assistant,
        "tool" => ChatRole.Tool,
        _ => ChatRole.User,
    };

    internal static CapturedRequest Parse(string path, string body)
    {
        var messages = new List<(string Role, string Content)>();
        if (!string.IsNullOrWhiteSpace(body)
            && JsonNode.Parse(body) is JsonObject root
            && root["messages"] is JsonArray messageArray)
        {
            foreach (var item in messageArray)
            {
                if (item is not JsonObject messageObject)
                {
                    continue;
                }

                var role = messageObject["role"]?.GetValue<string>() ?? "user";
                messages.Add((role, ExtractContent(messageObject["content"])));
            }
        }

        return new CapturedRequest(path, body, messages);
    }

    private static string ExtractContent(JsonNode? content)
    {
        switch (content)
        {
            case null:
                return string.Empty;
            case JsonValue value:
                return value.ToString();
            case JsonArray parts:
                var builder = new StringBuilder();
                foreach (var part in parts)
                {
                    if (part is JsonObject partObject && partObject["text"] is JsonValue text)
                    {
                        builder.Append(text.ToString());
                    }
                }

                return builder.ToString();
            default:
                return content.ToString();
        }
    }
}

/// <summary>
/// The routing handle for one named conversation: pairs a request matcher with the
/// <see cref="DeterministicTestChatClient"/> that scripts the conversation's responses. Tests
/// enqueue streaming responses (and express gating via the client's readiness mechanism) directly
/// on <see cref="Client"/>, and can observe each classified request through
/// <see cref="GetRequestAsync"/>.
/// </summary>
public sealed class ConversationClient
{
    private readonly Func<CapturedRequest, bool> matcher;
    private readonly object requestsLock = new();
    private readonly List<TaskCompletionSource<CapturedRequest>> requests = [];
    private int nextTurnIndex;

    internal ConversationClient(string name, Func<CapturedRequest, bool> matcher)
    {
        this.Name = name;
        this.matcher = matcher;
    }

    /// <summary>The conversation name used in diagnostics.</summary>
    public string Name { get; }

    /// <summary>The deterministic chat client scripting this conversation's responses.</summary>
    public DeterministicTestChatClient Client { get; } = new();

    /// <summary>
    /// Completes with the <paramref name="index"/>-th (zero-based) request classified into this
    /// conversation, letting tests observe request arrival and content.
    /// </summary>
    public Task<CapturedRequest> GetRequestAsync(int index)
    {
        lock (this.requestsLock)
        {
            this.EnsureRequestSlot(index);
            return this.requests[index].Task;
        }
    }

    internal bool Matches(CapturedRequest request) => this.matcher(request);

    internal int TakeNextTurnIndex() => Interlocked.Increment(ref this.nextTurnIndex) - 1;

    internal void CompleteRequest(CapturedRequest request)
    {
        lock (this.requestsLock)
        {
            this.EnsureRequestSlot(request.TurnIndex);
            this.requests[request.TurnIndex].TrySetResult(request);
        }
    }

    private void EnsureRequestSlot(int index)
    {
        while (this.requests.Count <= index)
        {
            this.requests.Add(new TaskCompletionSource<CapturedRequest>(
                TaskCreationOptions.RunContinuationsAsynchronously));
        }
    }
}
