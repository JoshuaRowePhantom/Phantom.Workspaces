namespace Phantom.Workspaces.Llm.Tests;

public sealed class AgentExecutionEnvironmentDispatcherTests
{
    [Fact]
    public async Task ExecuteToolCallAsync_WhenToolIsRegistered_DelegatesToMatchingExecutionEnvironment()
    {
        var expected = new LlmEvent
        {
            EventKind = LlmEventKinds.ToolResult,
            Role = LlmRoles.Tool,
            ToolName = "execute_command",
            Content = "ok",
        };
        var matchedInvoked = false;
        var otherInvoked = false;
        var dispatcher = new AgentExecutionEnvironmentDispatcher(
            new Dictionary<string, IAgentExecutionEnvironment>(StringComparer.Ordinal)
            {
                ["execute_command"] = new DelegateExecutionEnvironment(
                    (toolCall, _) =>
                    {
                        matchedInvoked = true;
                        Assert.Equal("execute_command", toolCall.ToolName);
                        return Task.FromResult(expected);
                    }),
                ["read_file"] = new DelegateExecutionEnvironment(
                    (_, _) =>
                    {
                        otherInvoked = true;
                        return Task.FromResult(
                            new LlmEvent
                            {
                                EventKind = LlmEventKinds.ToolResult,
                                Role = LlmRoles.Tool,
                                Content = "should not be called",
                            });
                    }),
            });

        var result = await dispatcher.ExecuteToolCallAsync(
            new LlmEvent
            {
                EventKind = LlmEventKinds.ToolCall,
                Role = LlmRoles.Assistant,
                ToolName = "execute_command",
            });

        Assert.Same(expected, result);
        Assert.True(matchedInvoked);
        Assert.False(otherInvoked);
    }

    [Fact]
    public async Task ExecuteToolCallAsync_WhenToolNameMissing_ReturnsFailureToolResult()
    {
        var dispatcher = new AgentExecutionEnvironmentDispatcher(
            new Dictionary<string, IAgentExecutionEnvironment>(StringComparer.Ordinal));

        var result = await dispatcher.ExecuteToolCallAsync(
            new LlmEvent
            {
                EventKind = LlmEventKinds.ToolCall,
                Role = LlmRoles.Assistant,
            });

        Assert.Equal(LlmEventKinds.ToolResult, result.EventKind);
        Assert.Equal(LlmRoles.Tool, result.Role);
        Assert.Contains("Tool name is missing", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteToolCallAsync_WhenToolIsNotRegistered_ReturnsFailureToolResult()
    {
        var knownInvoked = false;
        var dispatcher = new AgentExecutionEnvironmentDispatcher(
            new Dictionary<string, IAgentExecutionEnvironment>(StringComparer.Ordinal)
            {
                ["execute_command"] = new DelegateExecutionEnvironment(
                    (_, _) =>
                    {
                        knownInvoked = true;
                        return Task.FromResult(
                            new LlmEvent
                            {
                                EventKind = LlmEventKinds.ToolResult,
                                Role = LlmRoles.Tool,
                                Content = "should not be called",
                            });
                    }),
            });

        var result = await dispatcher.ExecuteToolCallAsync(
            new LlmEvent
            {
                EventKind = LlmEventKinds.ToolCall,
                Role = LlmRoles.Assistant,
                ToolName = "read_file",
            });

        Assert.False(knownInvoked);
        Assert.Equal(LlmEventKinds.ToolResult, result.EventKind);
        Assert.Equal(LlmRoles.Tool, result.Role);
        Assert.Equal("read_file", result.ToolName);
        Assert.Contains("No execution environment is registered", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Empty_WhenAnyToolRequested_ReturnsFailureToolResult()
    {
        var result = await AgentExecutionEnvironmentDispatcher.Empty.ExecuteToolCallAsync(
            new LlmEvent
            {
                EventKind = LlmEventKinds.ToolCall,
                Role = LlmRoles.Assistant,
                ToolName = "execute_command",
            });

        Assert.Equal(LlmEventKinds.ToolResult, result.EventKind);
        Assert.Equal(LlmRoles.Tool, result.Role);
        Assert.Equal("execute_command", result.ToolName);
        Assert.Contains("No execution environment is registered", result.Content, StringComparison.OrdinalIgnoreCase);
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
