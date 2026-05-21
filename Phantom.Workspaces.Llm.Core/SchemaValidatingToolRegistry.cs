namespace Phantom.Workspaces.Llm;

public sealed class SchemaValidatingToolRegistry : IToolRegistry
{
    private readonly IToolRegistry innerRegistry;

    public SchemaValidatingToolRegistry(
        IToolRegistry innerRegistry,
        Json.Schema.JsonSchema allowedToolCallSchema)
    {
        ArgumentNullException.ThrowIfNull(innerRegistry);
        ArgumentNullException.ThrowIfNull(allowedToolCallSchema);

        this.innerRegistry = innerRegistry;
    }

    public async Task<string> ExecuteToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolName);

        return await this.innerRegistry.ExecuteToolAsync(toolName, arguments, cancellationToken);
    }
}


