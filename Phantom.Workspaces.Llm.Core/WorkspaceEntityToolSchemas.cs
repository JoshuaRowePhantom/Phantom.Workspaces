using System.Text.Json;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Shared <see cref="SchemaDenormalizer"/> over the embedded workspace schema documents, used by the
/// workspace entity AI tools to produce self-sufficient input schemas (no external <c>$ref</c>s).
/// </summary>
internal static class WorkspaceEntityToolSchemas
{
    private static readonly SchemaDenormalizer Denormalizer =
        new(new EmbeddedSchemaDocumentResolver(typeof(IDataAccessLayer).Assembly));

    /// <summary>Denormalizes the given schema reference into a self-sufficient schema element.</summary>
    public static JsonElement Denormalize(string rootReference) => Denormalizer.Denormalize(rootReference);
}
