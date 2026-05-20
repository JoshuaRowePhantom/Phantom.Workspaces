namespace Phantom.Workspaces.Llm;

public sealed class AgentExecutionEnvironmentDispatcher : IAgentExecutionEnvironment
{
    public static AgentExecutionEnvironmentDispatcher Empty { get; } =
        new(Array.Empty<KeyValuePair<string, IAgentExecutionEnvironment>>());

    private readonly IReadOnlyDictionary<string, IAgentExecutionEnvironment> executionEnvironments;

    public AgentExecutionEnvironmentDispatcher(
        IEnumerable<KeyValuePair<string, IAgentExecutionEnvironment>> executionEnvironments)
    {
        ArgumentNullException.ThrowIfNull(executionEnvironments);

        this.executionEnvironments = executionEnvironments.ToDictionary(
            static pair => !string.IsNullOrWhiteSpace(pair.Key)
                ? pair.Key
                : throw new ArgumentException("Tool name cannot be null or whitespace.", nameof(executionEnvironments)),
            static pair => pair.Value
                ?? throw new ArgumentException("Tool execution environment cannot be null.", nameof(executionEnvironments)),
            StringComparer.Ordinal);
    }

    public async Task<LlmEvent> ExecuteToolCallAsync(
        LlmEvent toolCall,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        if (string.IsNullOrWhiteSpace(toolCall.ToolName))
        {
            return this.BuildFailureResult(toolCall, "Tool call failed dispatch. Tool name is missing.");
        }

        if (!this.executionEnvironments.TryGetValue(toolCall.ToolName, out var executionEnvironment))
        {
            return this.BuildFailureResult(
                toolCall,
                $"Tool call failed dispatch. No execution environment is registered for tool '{toolCall.ToolName}'.");
        }

        return await executionEnvironment.ExecuteToolCallAsync(toolCall, cancellationToken);
    }

    private LlmEvent BuildFailureResult(
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
