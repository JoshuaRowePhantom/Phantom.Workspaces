using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phantom.Workspaces.Data;

public sealed record AgentSessionRuntimeIntentData
{
    [JsonPropertyName("host-profile-entity-id")]
    public string? OwningProfileEntityId { get; init; } = null;

    [JsonPropertyName("ownership-generation")]
    public long OwnershipGeneration { get; init; } = 0;

    [JsonPropertyName("executor-bindings")]
    public JsonElement? ExecutorBindings { get; init; } = null;

    [JsonPropertyName("trust-profile-reference")]
    public string? TrustProfileReference { get; init; } = null;

    [JsonPropertyName("expected-trust-profile-revision")]
    public long? ExpectedTrustProfileRevision { get; init; } = null;

    [JsonPropertyName("continue-in-background")]
    public bool ContinueInBackground { get; init; } = false;
}
