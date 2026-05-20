using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Phantom.Workspaces.Llm;

public sealed class McpClientTransportAgentExecutionEnvironment : IAgentExecutionEnvironment, IAsyncDisposable
{
    private readonly Func<string, IReadOnlyDictionary<string, object?>?, CancellationToken, ValueTask<CallToolResult>> callToolAsync;
    private readonly Func<ValueTask> disposeAsync;

    public McpClientTransportAgentExecutionEnvironment(
        IClientTransport clientTransport,
        McpClientOptions? clientOptions = null)
    {
        ArgumentNullException.ThrowIfNull(clientTransport);

        var clientTask = new Lazy<Task<McpClient>>(
            () => McpClient.CreateAsync(clientTransport, clientOptions, cancellationToken: CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication);
        this.callToolAsync = async (toolName, arguments, cancellationToken) =>
        {
            var client = await clientTask.Value.WaitAsync(cancellationToken);
            return await client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken);
        };
        this.disposeAsync = async () =>
        {
            if (!clientTask.IsValueCreated)
            {
                return;
            }

            var client = await clientTask.Value;
            await client.DisposeAsync();
        };
    }

    internal McpClientTransportAgentExecutionEnvironment(
        Func<string, IReadOnlyDictionary<string, object?>?, CancellationToken, ValueTask<CallToolResult>> callToolAsync,
        Func<ValueTask>? disposeAsync = null)
    {
        this.callToolAsync = callToolAsync ?? throw new ArgumentNullException(nameof(callToolAsync));
        this.disposeAsync = disposeAsync ?? (() => ValueTask.CompletedTask);
    }

    public async Task<LlmEvent> ExecuteToolCallAsync(
        LlmEvent toolCall,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        if (string.IsNullOrWhiteSpace(toolCall.ToolName))
        {
            return CreateFailureResult(toolCall, "MCP tool execution failed. Tool name is missing.");
        }

        IReadOnlyDictionary<string, object?>? arguments;
        try
        {
            arguments = ParseArguments(toolCall.Content);
        }
        catch (JsonException jsonException)
        {
            return CreateFailureResult(toolCall, $"MCP tool execution failed. Invalid tool-call JSON arguments: {jsonException.Message}");
        }

        try
        {
            var result = await this.callToolAsync(toolCall.ToolName, arguments, cancellationToken);
            return CreateResultEvent(toolCall, result);
        }
        catch (McpException mcpException)
        {
            return CreateFailureResult(toolCall, $"MCP tool execution failed. {mcpException.Message}");
        }
    }

    public ValueTask DisposeAsync()
    {
        return this.disposeAsync();
    }

    private static IReadOnlyDictionary<string, object?>? ParseArguments(
        string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return null;
        }

        var jsonElement = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
        if (jsonElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Tool-call arguments must be a JSON object.");
        }

        return JsonSerializer.Deserialize<Dictionary<string, object?>>(jsonElement.GetRawText());
    }

    private static LlmEvent CreateResultEvent(
        LlmEvent toolCall,
        CallToolResult result)
    {
        var textContent = result.Content
            .OfType<TextContentBlock>()
            .Select(static content => content.Text)
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .ToArray();
        var text = string.Join(Environment.NewLine, textContent);
        if (string.IsNullOrWhiteSpace(text) && result.StructuredContent is not null)
        {
            text = result.StructuredContent.Value.GetRawText();
        }

        if (result.IsError == true)
        {
            text = string.IsNullOrWhiteSpace(text)
                ? "MCP tool execution failed."
                : $"MCP tool execution failed. {text}";
        }

        var now = DateTimeOffset.UtcNow;
        return new LlmEvent
        {
            StartTime = now,
            EndTime = now,
            Model = toolCall.Model,
            EventKind = LlmEventKinds.ToolResult,
            Role = LlmRoles.Tool,
            ToolName = toolCall.ToolName,
            CorrelationId = toolCall.CorrelationId,
            Content = text,
        };
    }

    private static LlmEvent CreateFailureResult(
        LlmEvent toolCall,
        string message)
    {
        var now = DateTimeOffset.UtcNow;
        return new LlmEvent
        {
            StartTime = now,
            EndTime = now,
            Model = toolCall.Model,
            EventKind = LlmEventKinds.ToolResult,
            Role = LlmRoles.Tool,
            ToolName = toolCall.ToolName,
            CorrelationId = toolCall.CorrelationId,
            Content = message,
        };
    }
}
