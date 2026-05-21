namespace Phantom.Workspaces.Llm;

/// <summary>
/// Registry of tools that can be executed during agent sessions.
/// </summary>
public interface IToolRegistry
{
    Task<string> ExecuteToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments = null,
        CancellationToken cancellationToken = default);
}

public sealed class ToolRegistry : IToolRegistry
{
    public static ToolRegistry Empty { get; } =
        new(Array.Empty<KeyValuePair<string, Func<IReadOnlyDictionary<string, object?>?, CancellationToken, ValueTask<string>>>>());

    private readonly IReadOnlyDictionary<string, Func<IReadOnlyDictionary<string, object?>?, CancellationToken, ValueTask<string>>> tools;

    public ToolRegistry(
        IEnumerable<KeyValuePair<string, Func<IReadOnlyDictionary<string, object?>?, CancellationToken, ValueTask<string>>>> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        this.tools = tools.ToDictionary(
            static pair => !string.IsNullOrWhiteSpace(pair.Key)
                ? pair.Key
                : throw new ArgumentException("Tool name cannot be null or whitespace.", nameof(tools)),
            static pair => pair.Value
                ?? throw new ArgumentException("Tool implementation cannot be null.", nameof(tools)),
            StringComparer.Ordinal);
    }

    public async Task<string> ExecuteToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolName);

        if (string.IsNullOrWhiteSpace(toolName))
        {
            return "Tool execution failed. Tool name is empty.";
        }

        if (!this.tools.TryGetValue(toolName, out var toolImplementation))
        {
            return $"Tool execution failed. No tool is registered for '{toolName}'.";
        }

        try
        {
            return await toolImplementation(arguments, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return "Tool execution was cancelled.";
        }
        catch (Exception exception)
        {
            return $"Tool execution failed. {exception.Message}";
        }
    }
}
