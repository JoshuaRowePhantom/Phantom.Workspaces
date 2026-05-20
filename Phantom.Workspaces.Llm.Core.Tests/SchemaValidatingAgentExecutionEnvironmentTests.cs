using Json.Schema;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class SchemaValidatingAgentExecutionEnvironmentTests
{
    [Fact]
    public async Task ExecuteToolCallAsync_WhenSchemaValid_DelegatesToUnderlying()
    {
        var expected = new LlmEvent
        {
            EventKind = LlmEventKinds.ToolResult,
            Role = LlmRoles.Tool,
            ToolName = "execute_command",
            Content = """{"ok":true}""",
        };
        var underlying = new DelegateExecutionEnvironment((_, _) => Task.FromResult(expected));
        var schema = new JsonSchemaBuilder()
            .Type(SchemaValueType.Object)
            .Properties(
                ("EventKind", new JsonSchemaBuilder().Const(LlmEventKinds.ToolCall)),
                ("ToolName", new JsonSchemaBuilder().Type(SchemaValueType.String)))
            .Required("EventKind", "ToolName")
            .Build();

        var validator = new SchemaValidatingAgentExecutionEnvironment(underlying, schema);
        var result = await validator.ExecuteToolCallAsync(
            new LlmEvent
            {
                EventKind = LlmEventKinds.ToolCall,
                Role = LlmRoles.Assistant,
                ToolName = "execute_command",
            });

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task ExecuteToolCallAsync_WhenSchemaInvalid_ReturnsValidationFailureToolResult()
    {
        var underlyingCalled = false;
        var underlying = new DelegateExecutionEnvironment(
            (_, _) =>
            {
                underlyingCalled = true;
                return Task.FromResult(
                    new LlmEvent
                    {
                        EventKind = LlmEventKinds.ToolResult,
                        Role = LlmRoles.Tool,
                        Content = "unexpected",
                    });
            });
        var schema = new JsonSchemaBuilder()
            .Type(SchemaValueType.Object)
            .Properties(
                ("ToolName", new JsonSchemaBuilder().Type(SchemaValueType.String)))
            .Required("ToolName")
            .Build();

        var validator = new SchemaValidatingAgentExecutionEnvironment(underlying, schema);
        var result = await validator.ExecuteToolCallAsync(
            new LlmEvent
            {
                EventKind = LlmEventKinds.ToolCall,
                Role = LlmRoles.Assistant,
                Content = """{"command":"dotnet build"}""",
            });

        Assert.False(underlyingCalled);
        Assert.Equal(LlmEventKinds.ToolResult, result.EventKind);
        Assert.Equal(LlmRoles.Tool, result.Role);
        Assert.Contains("failed schema validation", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ToolName", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class DelegateExecutionEnvironment(
        Func<LlmEvent, CancellationToken, Task<LlmEvent>> handler) : IAgentExecutionEnvironment
    {
        public Task<LlmEvent> ExecuteToolCallAsync(
            LlmEvent toolCall,
            CancellationToken cancellationToken = default)
        {
            return handler(toolCall, cancellationToken);
        }
    }
}
