using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phantom.Workspaces.Install;

/// <summary>
/// Metadata written into the managed layout so subsequent launches know they are "managed".
/// </summary>
public sealed record InstallMetadata
{
    /// <summary>The installed version.</summary>
    [JsonPropertyName("version")]
    public required string Version { get; init; }

    /// <summary>The release channel (stable by default).</summary>
    [JsonPropertyName("channel")]
    public string Channel { get; init; } = "stable";

    /// <summary>When the install/bootstrap occurred.</summary>
    [JsonPropertyName("installedAtUtc")]
    public required DateTimeOffset InstalledAtUtc { get; init; }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>Serializes this metadata to indented JSON.</summary>
    public string ToJson()
    {
        return JsonSerializer.Serialize(this, SerializerOptions);
    }

    /// <summary>Deserializes metadata from JSON, or <c>null</c> when invalid.</summary>
    public static InstallMetadata? FromJson(string json)
    {
        return JsonSerializer.Deserialize<InstallMetadata>(json, SerializerOptions);
    }
}
