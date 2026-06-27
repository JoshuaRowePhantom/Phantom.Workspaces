using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// A request to open a generic bidirectional <see cref="System.IO.Stream"/> under a resolved trust
/// profile on a target client instance. The <see cref="StreamKind"/> selects the handler;
/// <see cref="OpenPayload"/> carries kind-specific start parameters (e.g. shell start args).
/// </summary>
public sealed record TrustedStreamRequest
{
    /// <summary>The client instance to open the stream on; <c>"."</c> denotes the local instance.</summary>
    [JsonPropertyName("targetClientInstance")]
    public required string TargetClientInstance { get; init; }

    /// <summary>The stream kind, e.g. <c>"shell"</c>.</summary>
    [JsonPropertyName("streamKind")]
    public required string StreamKind { get; init; }

    /// <summary>Kind-specific open parameters.</summary>
    [JsonPropertyName("openPayload")]
    public required JsonElement OpenPayload { get; init; }
}
