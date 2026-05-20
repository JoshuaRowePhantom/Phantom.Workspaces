namespace Phantom.Workspaces.Llm.Provider.Llama;

public static class OllamaThinkingLevel
{
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
    public const string True = "true";
    public const string False = "false";
}

public sealed class OllamaOptions
{
    public const string LocalEndpoint = "http://localhost:11434/api/chat";

    public string Model { get; init; } = "qwen3.6";

    public Uri Endpoint { get; init; } = new(LocalEndpoint);

    public string? ThinkingLevel { get; init; }

    public int? ContextSize { get; init; }
}
