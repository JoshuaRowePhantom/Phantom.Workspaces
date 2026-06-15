using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phantom.Workspaces.Data;

/// <summary>
/// Canonical JSON serializer options shared by the web data-access client and server so their
/// request/response wire formats are symmetric. The data-access DTOs rely on default
/// (PascalCase) naming for unattributed members and explicit <see cref="JsonPropertyNameAttribute"/>
/// for the rest; both sides must therefore use the same naming policy.
/// </summary>
public static class WebDataAccessJsonSerialization
{
    /// <summary>The shared serializer options for web data-access transport.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
