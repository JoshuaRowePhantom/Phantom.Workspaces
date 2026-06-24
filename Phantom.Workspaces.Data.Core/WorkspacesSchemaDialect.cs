using Json.Schema;

namespace Phantom.Workspaces.Data;

/// <summary>
/// The JSON Schema dialect used to build and evaluate workspace entity schemas. It is the library's
/// default dialect extended to permit unknown keywords, so that the custom annotation keywords the
/// codebase relies on (for example <c>x-entity-types</c>, <c>x-default-mime-type</c> and
/// <c>x-field-status</c>) are legal JSON Schema and flow through to the field-type resolver, rather
/// than being stripped before validation.
/// </summary>
internal static class WorkspacesSchemaDialect
{
    public static Dialect AllowingUnknownKeywords { get; } =
        Dialect.Default.With(System.Array.Empty<IKeywordHandler>(), null, null, true);
}
