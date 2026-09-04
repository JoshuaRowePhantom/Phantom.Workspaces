using System.Text.Json.Serialization;
using AgentSchema;
using GitHub.Copilot;

namespace Phantom.Workspaces.Llm;

public sealed record BuiltinToolSet(
    [property: JsonPropertyName("tools")] IReadOnlyList<string>? Tools = null,
    [property: JsonPropertyName("isolated")] bool Isolated = false);

public sealed class GitHubCliBuiltinToolsTool : CustomTool
{
    public const string KindName = "github-cli-builtin-tools";

    [JsonPropertyName("available-tools")]
    public BuiltinToolSet? AvailableTools { get; init; }

    [JsonPropertyName("excluded-tools")]
    public BuiltinToolSet? ExcludedTools { get; init; }

    [JsonPropertyName("client-mode")]
    public CopilotClientMode ClientMode { get; init; } = CopilotClientMode.CopilotCli;
}

public sealed record CopilotBuiltinToolPolicy(
    IReadOnlyList<string>? AvailableTools,
    IReadOnlyList<string>? ExcludedTools,
    CopilotClientMode ClientMode);
