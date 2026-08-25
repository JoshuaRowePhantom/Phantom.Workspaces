using System.Text.Json.Serialization;

namespace Phantom.Workspaces.Tools;

/// <summary>
/// Strongly-typed model of the single-line JSON object emitted by <c>code tunnel status</c>.
/// Mirrors the upstream Rust <c>StatusOutput</c> struct (microsoft/vscode
/// <c>cli/src/commands/tunnels.rs</c>). The outer <see cref="Tunnel"/> member is <see langword="null"/>
/// when no tunnel daemon is running and an object when it is running; <see cref="ServiceInstalled"/>
/// is a separate OS-service flag and is NOT a liveness signal.
/// </summary>
internal sealed record VsCodeTunnelStatusOutput
{
    [JsonPropertyName("tunnel")]
    public VsCodeTunnelDaemonStatus? Tunnel { get; init; }

    [JsonPropertyName("service_installed")]
    public bool ServiceInstalled { get; init; }
}

/// <summary>
/// Strongly-typed model of the inner tunnel status object (upstream Rust
/// <c>StatusWithTunnelName</c> flattened with <c>Status</c>). The <see cref="Tunnel"/> field is the
/// connection-health string (<c>"Connected"</c> or <c>"Disconnected"</c>) and is deliberately typed
/// as a tolerant <see cref="string"/> so an unknown/future value does not throw and is treated as
/// not-connected.
/// </summary>
internal sealed record VsCodeTunnelDaemonStatus
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("started_at")]
    public string? StartedAt { get; init; }

    [JsonPropertyName("tunnel")]
    public string? Tunnel { get; init; }

    [JsonPropertyName("last_connected_at")]
    public string? LastConnectedAt { get; init; }

    [JsonPropertyName("last_disconnected_at")]
    public string? LastDisconnectedAt { get; init; }

    [JsonPropertyName("last_fail_reason")]
    public string? LastFailReason { get; init; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(VsCodeTunnelStatusOutput))]
internal sealed partial class VsCodeTunnelStatusJsonContext : JsonSerializerContext
{
}
