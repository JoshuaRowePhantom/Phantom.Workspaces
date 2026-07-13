using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// A deterministic, scripted OpenAI-compatible chat-completions server for full-stack BYOK tests
/// (issue #912). The GitHub Copilot CLI issues multiple distinct conversations against the same
/// endpoint (the main session plus one per sub-agent); this server classifies each incoming
/// request by inspecting its message content, routes it to a named per-conversation script with
/// its own turn counter, and streams scripted SSE replies (text and OpenAI <c>tool_calls</c>
/// deltas). Every scripted step can carry awaitable gates so a test controls exactly when each
/// reply — or any point inside a streamed reply — is delivered. Requests that match no
/// conversation script, or that arrive after a conversation's script is exhausted, fail loudly:
/// the server responds 500 and records a diagnostic in <see cref="Failures"/> (it never hangs).
/// </summary>
public sealed class ScriptedByokChatServer : IAsyncDisposable
{
    private readonly HttpListener listener;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task acceptLoop;
    private readonly List<ScriptedConversation> conversations = [];
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
    /// Registers a named conversation script. Incoming requests are classified by evaluating each
    /// conversation's <paramref name="matcher"/> in registration order; the first match wins and
    /// the request consumes that conversation's next scripted turn.
    /// </summary>
    public ScriptedConversation AddConversation(string name, Func<CapturedRequest, bool> matcher)
    {
        var conversation = new ScriptedConversation(name, matcher);
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

            ScriptedConversation? conversation = null;
            ScriptedTurn? turn = null;
            lock (this.conversationsLock)
            {
                foreach (var candidate in this.conversations)
                {
                    if (candidate.Matches(request))
                    {
                        conversation = candidate;
                        turn = candidate.TakeNextTurn();
                        break;
                    }
                }
            }

            if (conversation is null)
            {
                this.RecordFailure($"No conversation script matched request. Body: {Truncate(body)}");
                await WriteJsonAsync(context, 500, "{\"error\":\"no conversation script matched\"}").ConfigureAwait(false);
                return;
            }

            if (turn is null)
            {
                this.RecordFailure(
                    $"Conversation '{conversation.Name}' has no scripted turns left. Body: {Truncate(body)}");
                await WriteJsonAsync(context, 500, "{\"error\":\"conversation script exhausted\"}").ConfigureAwait(false);
                return;
            }

            request.Conversation = conversation.Name;
            request.TurnIndex = turn.Index;
            this.Trace?.Invoke($"MATCH conversation='{conversation.Name}' turn={turn.Index}");
            turn.SetRequest(request);

            await this.WriteScriptedSseAsync(context, turn).ConfigureAwait(false);
            this.Trace?.Invoke($"REPLY-COMPLETE conversation='{conversation.Name}' turn={turn.Index}");
        }
        catch (Exception exception)
        {
            this.RecordFailure($"Request handling threw: {exception}");
            try
            {
                await WriteJsonAsync(
                    context,
                    500,
                    $"{{\"error\":{System.Text.Json.JsonSerializer.Serialize(exception.Message)}}}").ConfigureAwait(false);
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

    private async Task WriteScriptedSseAsync(HttpListenerContext context, ScriptedTurn turn)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/event-stream";
        context.Response.SendChunked = true;
        var output = context.Response.OutputStream;

        var completionId = $"chatcmpl-{turn.Index}-{Guid.NewGuid():n}";
        var wroteRole = false;

        foreach (var item in turn.Items)
        {
            switch (item)
            {
                case ScriptGateItem gate:
                    await gate.Gate.Released.WaitAsync(this.cancellation.Token).ConfigureAwait(false);
                    break;

                case ScriptTextItem text:
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

                case ScriptToolCallItem toolCall:
                    // OpenAI streaming tool_calls: first delta carries id/type/name, argument
                    // fragments follow with the same index. Emit the header and the full argument
                    // payload as two chunks so the client exercises its fragment-joining path.
                    await WriteSseEventAsync(output, BuildChunk(completionId, delta =>
                    {
                        if (!wroteRole)
                        {
                            delta["role"] = "assistant";
                        }

                        delta["tool_calls"] = new JsonArray(new JsonObject
                        {
                            ["index"] = toolCall.Index,
                            ["id"] = toolCall.CallId,
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = toolCall.Name,
                                ["arguments"] = string.Empty,
                            },
                        });
                    })).ConfigureAwait(false);
                    wroteRole = true;

                    await WriteSseEventAsync(output, BuildChunk(completionId, delta =>
                    {
                        delta["tool_calls"] = new JsonArray(new JsonObject
                        {
                            ["index"] = toolCall.Index,
                            ["function"] = new JsonObject
                            {
                                ["arguments"] = toolCall.ArgumentsJson,
                            },
                        });
                    })).ConfigureAwait(false);
                    break;

                default:
                    throw new InvalidOperationException($"Unknown script item type '{item.GetType()}'.");
            }
        }

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
                ["finish_reason"] = turn.FinishReason,
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

    /// <summary>The zero-based scripted turn index consumed by this request.</summary>
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

/// <summary>A named per-conversation script with its own turn counter.</summary>
public sealed class ScriptedConversation
{
    private readonly Func<CapturedRequest, bool> matcher;
    private readonly List<ScriptedTurn> turns = [];
    private int nextTurn;

    internal ScriptedConversation(string name, Func<CapturedRequest, bool> matcher)
    {
        this.Name = name;
        this.matcher = matcher;
    }

    /// <summary>The conversation name used in diagnostics.</summary>
    public string Name { get; }

    /// <summary>Appends a scripted turn; requests consume turns in order.</summary>
    public ScriptedTurn AddTurn()
    {
        var turn = new ScriptedTurn(this.turns.Count);
        this.turns.Add(turn);
        return turn;
    }

    internal bool Matches(CapturedRequest request) => this.matcher(request);

    internal ScriptedTurn? TakeNextTurn()
    {
        if (this.nextTurn >= this.turns.Count)
        {
            return null;
        }

        return this.turns[this.nextTurn++];
    }
}

/// <summary>An awaitable gate a test releases to let the server proceed past a scripted point.</summary>
public sealed class ScriptGate
{
    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the test has released this gate.</summary>
    public Task Released => this.released.Task;

    /// <summary>Releases the gate, letting the scripted reply proceed.</summary>
    public void Release() => this.released.TrySetResult();
}

/// <summary>
/// One scripted assistant reply: an ordered list of stream items (text deltas, tool-call deltas,
/// and gates) plus the finish reason. The turn's <see cref="Request"/> task completes when a
/// request consumes this turn, letting tests observe arrival before releasing gates.
/// </summary>
public sealed class ScriptedTurn
{
    private readonly TaskCompletionSource<CapturedRequest> request = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<object> items = [];

    internal ScriptedTurn(int index) => this.Index = index;

    /// <summary>The zero-based index of this turn within its conversation.</summary>
    public int Index { get; }

    /// <summary>Completes with the request that consumed this turn.</summary>
    public Task<CapturedRequest> Request => this.request.Task;

    /// <summary>The finish reason for the final SSE chunk; defaults to <c>stop</c>.</summary>
    public string FinishReason { get; set; } = "stop";

    internal IReadOnlyList<object> Items => this.items;

    /// <summary>Appends a streamed text delta.</summary>
    public ScriptedTurn AddText(string text)
    {
        this.items.Add(new ScriptTextItem(text));
        return this;
    }

    /// <summary>Appends a streamed OpenAI tool call and sets the finish reason to <c>tool_calls</c>.</summary>
    public ScriptedTurn AddToolCall(int index, string callId, string name, string argumentsJson)
    {
        this.items.Add(new ScriptToolCallItem(index, callId, name, argumentsJson));
        this.FinishReason = "tool_calls";
        return this;
    }

    /// <summary>Appends an awaitable gate; the stream stalls at this point until released.</summary>
    public ScriptGate AddGate()
    {
        var gate = new ScriptGate();
        this.items.Add(new ScriptGateItem(gate));
        return gate;
    }

    internal void SetRequest(CapturedRequest capturedRequest) => this.request.TrySetResult(capturedRequest);
}

internal sealed record ScriptTextItem(string Text);

internal sealed record ScriptToolCallItem(int Index, string CallId, string Name, string ArgumentsJson);

internal sealed record ScriptGateItem(ScriptGate Gate);
