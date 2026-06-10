using System.Text.Json;

namespace Phantom.Workspaces.Data;

public sealed class ResolvedFieldType
{
    public required string TypeName { get; init; }

    public string? DefaultMimeType { get; init; }

    public IReadOnlyCollection<string> EntityTypes { get; init; } = Array.Empty<string>();

    public JsonElement? SchemaNode { get; init; }
}

