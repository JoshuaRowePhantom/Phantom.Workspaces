namespace Phantom.Workspaces.Data;

/// <summary>
/// Status annotation read from a field schema's <c>x-field-status</c> keyword. Declares the status
/// values that should render as "good" (green) and "bad" (red). Any other value is given a stable,
/// distinct color derived by hashing the value. Matching is case-sensitive (both the schema lists
/// and the field value are stored data).
/// </summary>
public sealed record FieldStatus(
    IReadOnlyCollection<string> GoodStatusValues,
    IReadOnlyCollection<string> BadStatusValues);
