using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// A minimal OpenAI-compatible chat-completions server that fronts an arbitrary
/// <see cref="IChatClient"/> (for example, the echo test provider). It lets the GitHub Copilot
/// provider be exercised in BYOK mode against one of our own test chat providers.
/// </summary>
/// <remarks>
/// Implemented over <see cref="HttpListener"/> on a <c>http://localhost:{port}/</c> prefix, which
/// non-administrator users can bind on Windows. Handles a non-streaming chat-completion request
/// at any path under the prefix (for example, <c>/chat/completions</c> or
/// <c>/v1/chat/completions</c>).
/// </remarks>
public sealed class OpenAiCompatibleChatServer : IAsyncDisposable
{
    private readonly IChatClient chatClient;
    private readonly HttpListener listener;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task acceptLoop;

    /// <summary>Starts the server fronting the supplied chat client on a free loopback port.</summary>
    public OpenAiCompatibleChatServer(IChatClient chatClient)
    {
        this.chatClient = chatClient;
        var port = GetFreePort();
        this.BaseUrl = $"http://localhost:{port}/";
        this.listener = new HttpListener();
        this.listener.Prefixes.Add(this.BaseUrl);
        this.listener.Start();
        this.acceptLoop = Task.Run(() => this.AcceptLoopAsync(this.cancellation.Token));
    }

    /// <summary>The absolute base URL the server is listening on.</summary>
    public string BaseUrl { get; }

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

            var messages = ParseMessages(body);
            var response = await this.chatClient.GetResponseAsync(messages).ConfigureAwait(false);

            if (IsStreamingRequest(body))
            {
                await WriteSseAsync(context, response.Text).ConfigureAwait(false);
            }
            else
            {
                await WriteJsonAsync(context, 200, BuildCompletionJson(response.Text)).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            try
            {
                await WriteJsonAsync(context, 500, $"{{\"error\":{JsonSerializer.Serialize(exception.Message)}}}").ConfigureAwait(false);
            }
            catch
            {
                // Best effort: the connection may already be gone.
            }
        }
    }

    private static bool IsStreamingRequest(string requestBody)
    {
        if (string.IsNullOrWhiteSpace(requestBody))
        {
            return false;
        }

        return JsonNode.Parse(requestBody) is JsonObject root
            && root["stream"] is JsonValue streamValue
            && streamValue.TryGetValue(out bool stream)
            && stream;
    }

    private static async Task WriteSseAsync(HttpListenerContext context, string content)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/event-stream";
        context.Response.SendChunked = true;

        var contentChunk = new JsonObject
        {
            ["id"] = "chatcmpl-test",
            ["object"] = "chat.completion.chunk",
            ["created"] = 0,
            ["model"] = "test",
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = new JsonObject { ["role"] = "assistant", ["content"] = content },
                    ["finish_reason"] = null,
                },
            },
        };

        var finalChunk = new JsonObject
        {
            ["id"] = "chatcmpl-test",
            ["object"] = "chat.completion.chunk",
            ["created"] = 0,
            ["model"] = "test",
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = new JsonObject(),
                    ["finish_reason"] = "stop",
                },
            },
        };

        var outputStream = context.Response.OutputStream;
        await WriteSseEventAsync(outputStream, contentChunk.ToJsonString()).ConfigureAwait(false);
        await WriteSseEventAsync(outputStream, finalChunk.ToJsonString()).ConfigureAwait(false);
        await WriteSseEventAsync(outputStream, "[DONE]").ConfigureAwait(false);
        outputStream.Close();
        context.Response.Close();
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

    private static List<ChatMessage> ParseMessages(string requestBody)
    {
        var messages = new List<ChatMessage>();
        if (string.IsNullOrWhiteSpace(requestBody))
        {
            return messages;
        }

        if (JsonNode.Parse(requestBody) is not JsonObject root || root["messages"] is not JsonArray messageArray)
        {
            return messages;
        }

        foreach (var item in messageArray)
        {
            if (item is not JsonObject messageObject)
            {
                continue;
            }

            var role = messageObject["role"]?.GetValue<string>() ?? "user";
            var content = ExtractContent(messageObject["content"]);
            messages.Add(new ChatMessage(MapRole(role), content));
        }

        return messages;
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

    private static ChatRole MapRole(string role) => role switch
    {
        "system" => ChatRole.System,
        "assistant" => ChatRole.Assistant,
        "tool" => ChatRole.Tool,
        _ => ChatRole.User,
    };

    private static string BuildCompletionJson(string content)
    {
        var payload = new JsonObject
        {
            ["id"] = "chatcmpl-test",
            ["object"] = "chat.completion",
            ["created"] = 0,
            ["model"] = "test",
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = content,
                    },
                    ["finish_reason"] = "stop",
                },
            },
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = 0,
                ["completion_tokens"] = 0,
                ["total_tokens"] = 0,
            },
        };

        return payload.ToJsonString();
    }
}
