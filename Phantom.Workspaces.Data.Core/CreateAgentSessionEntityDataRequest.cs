using System.Text.Json;

namespace Phantom.Workspaces.Data;

public sealed record CreateAgentSessionEntityDataRequest
{
    public required EntityId AgentDefinitionEntityId { get; init; }

    public required string AgentDisplayName { get; init; }

    public required string AgentSessionId { get; init; }

    public required IReadOnlyCollection<EntityName> AgentSessionNames { get; init; }

    public required DateTimeOffset CurrentTime { get; init; }

    public required string ComputerName { get; init; }

    public required EntityId HostProfileEntityId { get; init; }

    public IReadOnlyDictionary<string, string>? ParameterValues { get; init; } = null;

    public JsonElement? SessionExecutor { get; init; } = null;

    public JsonElement? ExecutorComponentBindings { get; init; } = null;

    public IReadOnlyDictionary<string, JsonElement>? ParameterSelections { get; init; } = null;

    public long OwnershipGeneration { get; init; } = 0;

    public JsonElement? TrustProfileReference { get; init; } = null;

    public string? ExpectedTrustProfileRevision { get; init; } = null;

    public bool ContinueInBackground { get; init; } = false;
}
