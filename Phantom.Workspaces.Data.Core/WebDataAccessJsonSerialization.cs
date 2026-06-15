using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phantom.Workspaces.Data;

/// <summary>
/// Canonical JSON serializer options shared by the web data-access client and server so their
/// request/response wire formats are symmetric. Every data-access DTO member that crosses the
/// wire carries an explicit <see cref="JsonPropertyNameAttribute"/>, so the wire format does not
/// depend on a naming policy; these options only control null handling.
/// </summary>
public static class WebDataAccessJsonSerialization
{
    /// <summary>The shared serializer options for web data-access transport.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
