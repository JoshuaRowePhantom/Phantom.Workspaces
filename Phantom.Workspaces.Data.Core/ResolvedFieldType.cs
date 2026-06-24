using System.Text.Json;

namespace Phantom.Workspaces.Data;

public sealed class ResolvedFieldType
{
    public required string TypeName { get; init; }

    public string? DefaultMimeType { get; init; }

    public IReadOnlyCollection<string> EntityTypes { get; init; } = Array.Empty<string>();

    public JsonElement? SchemaNode { get; init; }

    /// <summary>
    /// Optional custom field-editor selector read from the schema's <c>x-field-editor</c>
    /// keyword. May be a registered short name or an assembly-qualified type name.
    /// </summary>
    public string? FieldEditorTypeName { get; init; }

    /// <summary>
    /// Optional absolute display order read from the schema's
    /// <c>x-absolute-entity-display-order</c> keyword. Absolute-ordered fields sort strictly
    /// by this value and render before all other fields and entities.
    /// </summary>
    public double? AbsoluteEntityDisplayOrder { get; init; }

    /// <summary>
    /// Relative display order within the field's own entity type group, read from the schema's
    /// <c>x-relative-entity-display-order</c> keyword. Defaults to 0 when absent.
    /// </summary>
    public double RelativeEntityDisplayOrder { get; init; }

    /// <summary>
    /// Optional status annotation read from the schema's <c>x-field-status</c> keyword. Present only
    /// for fields whose value represents a status; used to render colored status badges on the card.
    /// </summary>
    public FieldStatus? FieldStatus { get; init; }
}

