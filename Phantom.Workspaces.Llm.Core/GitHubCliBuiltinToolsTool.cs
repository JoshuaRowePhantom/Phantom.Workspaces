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

    public override Dictionary<string, object?> Save(SaveContext? context = null)
    {
        var saved = base.Save(context!);
        // CustomTool emits an empty options bag; policy fields belong to this typed tool,
        // not to ignored custom options.
        saved.Remove("options");
        saved["client-mode"] = this.ClientMode == CopilotClientMode.Empty ? "empty" : "copilot-cli";
        if (this.AvailableTools is { } available)
        {
            saved["available-tools"] = SaveSelector(available);
        }
        if (this.ExcludedTools is { } excluded)
        {
            saved["excluded-tools"] = SaveSelector(excluded);
        }
        return saved;
    }

    private static Dictionary<string, object?> SaveSelector(BuiltinToolSet selector)
        => selector.Isolated
            ? new Dictionary<string, object?> { ["isolated"] = true }
            : new Dictionary<string, object?> { ["tools"] = selector.Tools };
}

public sealed record CopilotBuiltinToolPolicy(
    IReadOnlyList<string>? AvailableTools,
    IReadOnlyList<string>? ExcludedTools,
    CopilotClientMode ClientMode);
