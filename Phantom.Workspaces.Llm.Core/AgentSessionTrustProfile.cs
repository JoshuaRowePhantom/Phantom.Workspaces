using Json.Schema;

namespace Phantom.Workspaces.Llm;

public sealed class AgentSessionTrustProfile
{
    public static AgentSessionTrustProfile Empty { get; } =
        new(Array.Empty<KeyValuePair<string, IAgentExecutionEnvironment>>());

    private readonly IReadOnlyDictionary<string, IAgentExecutionEnvironment> toolExecutionEnvironments;

    private readonly JsonSchema? allowedToolCallSchema;
    private readonly Action<SchemaRegistry>? schemaResolver;

    public AgentSessionTrustProfile(
        IEnumerable<KeyValuePair<string, IAgentExecutionEnvironment>> toolExecutionEnvironments,
        JsonSchema? allowedToolCallSchema = null,
        Action<SchemaRegistry>? schemaResolver = null)
    {
        ArgumentNullException.ThrowIfNull(toolExecutionEnvironments);

        this.toolExecutionEnvironments = toolExecutionEnvironments.ToDictionary(
            static pair => !string.IsNullOrWhiteSpace(pair.Key)
                ? pair.Key
                : throw new ArgumentException("Tool name cannot be null or whitespace.", nameof(toolExecutionEnvironments)),
            static pair => pair.Value
                ?? throw new ArgumentException("Tool execution environment cannot be null.", nameof(toolExecutionEnvironments)),
            StringComparer.Ordinal);
        this.allowedToolCallSchema = allowedToolCallSchema;
        this.schemaResolver = schemaResolver;
    }

    public IReadOnlyDictionary<string, IAgentExecutionEnvironment> ToolExecutionEnvironments =>
        this.toolExecutionEnvironments;

    public IAgentExecutionEnvironment CreateExecutionEnvironment()
    {
        IAgentExecutionEnvironment executionEnvironment =
            new AgentExecutionEnvironmentDispatcher(this.toolExecutionEnvironments);
        if (this.allowedToolCallSchema is null)
        {
            return executionEnvironment;
        }

        return new SchemaValidatingAgentExecutionEnvironment(
            executionEnvironment,
            this.allowedToolCallSchema,
            this.schemaResolver);
    }
}
