using Json.Schema;

namespace Phantom.Workspaces.Llm;

public sealed class AgentSessionTrustProfile
{
    public static AgentSessionTrustProfile Empty { get; } =
        new(Array.Empty<KeyValuePair<string, Func<IReadOnlyDictionary<string, object?>?, CancellationToken, ValueTask<string>>>>());

    private readonly IReadOnlyDictionary<string, Func<IReadOnlyDictionary<string, object?>?, CancellationToken, ValueTask<string>>> tools;

    private readonly JsonSchema? allowedToolCallSchema;

    public AgentSessionTrustProfile(
        IEnumerable<KeyValuePair<string, Func<IReadOnlyDictionary<string, object?>?, CancellationToken, ValueTask<string>>>> tools,
        JsonSchema? allowedToolCallSchema = null)
    {
        ArgumentNullException.ThrowIfNull(tools);

        this.tools = tools.ToDictionary(
            static pair => !string.IsNullOrWhiteSpace(pair.Key)
                ? pair.Key
                : throw new ArgumentException("Tool name cannot be null or whitespace.", nameof(tools)),
            static pair => pair.Value
                ?? throw new ArgumentException("Tool implementation cannot be null.", nameof(tools)),
            StringComparer.Ordinal);
        this.allowedToolCallSchema = allowedToolCallSchema;
    }

    public IReadOnlyDictionary<string, Func<IReadOnlyDictionary<string, object?>?, CancellationToken, ValueTask<string>>> Tools =>
        this.tools;

    public IToolRegistry CreateToolRegistry()
    {
        var toolRegistry = new ToolRegistry(this.tools);
        if (this.allowedToolCallSchema is null)
        {
            return toolRegistry;
        }

        return new SchemaValidatingToolRegistry(
            toolRegistry,
            this.allowedToolCallSchema);
    }
}

