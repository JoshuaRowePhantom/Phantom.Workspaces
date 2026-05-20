using Json.Schema;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class AgentSessionTrustProfileTests
{
    [Fact]
    public async Task Empty_CreateExecutionEnvironment_DispatchesNoTools()
    {
        var executionEnvironment = AgentSessionTrustProfile.Empty.CreateExecutionEnvironment();

        var result = await executionEnvironment.ExecuteToolCallAsync(
            new LlmEvent
            {
                EventKind = LlmEventKinds.ToolCall,
                Role = LlmRoles.Assistant,
                ToolName = "read_file",
            });

        Assert.Equal(LlmEventKinds.ToolResult, result.EventKind);
        Assert.Contains("No execution environment is registered", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateExecutionEnvironment_WithRegisteredTool_RoutesToMatchingEnvironment()
    {
        var expected = new LlmEvent
        {
            EventKind = LlmEventKinds.ToolResult,
            Role = LlmRoles.Tool,
            Content = "ok",
        };
        var profile = new AgentSessionTrustProfile(
        [
            new KeyValuePair<string, IAgentExecutionEnvironment>(
                "read_file",
                new DelegateExecutionEnvironment((_, _) => Task.FromResult(expected))),
        ]);

        var result = await profile.CreateExecutionEnvironment().ExecuteToolCallAsync(
            new LlmEvent
            {
                EventKind = LlmEventKinds.ToolCall,
                Role = LlmRoles.Assistant,
                ToolName = "read_file",
            });

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task CreateExecutionEnvironment_WithSchema_ValidatesBeforeDispatch()
    {
        var underlyingCalled = false;
        var profile = new AgentSessionTrustProfile(
        [
            new KeyValuePair<string, IAgentExecutionEnvironment>(
                "read_file",
                new DelegateExecutionEnvironment(
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
                    })),
        ],
        new JsonSchemaBuilder()
            .Type(SchemaValueType.Object)
            .Properties(
                ("ToolName", new JsonSchemaBuilder().Type(SchemaValueType.String)))
            .Required("ToolName")
            .Build());

        var result = await profile.CreateExecutionEnvironment().ExecuteToolCallAsync(
            new LlmEvent
            {
                EventKind = LlmEventKinds.ToolCall,
                Role = LlmRoles.Assistant,
                Content = """{"path":"README.md"}""",
            });

        Assert.False(underlyingCalled);
        Assert.Equal(LlmEventKinds.ToolResult, result.EventKind);
        Assert.Contains("failed schema validation", result.Content, StringComparison.OrdinalIgnoreCase);
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
