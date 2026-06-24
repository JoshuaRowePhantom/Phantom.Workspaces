using System.Linq;
using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class EntityCardFieldBuildingTests
{
    [AvaloniaFact]
    public async Task FieldEditorFactory_BuildsMarkdownEditorForNoteContent()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);
        var entityTypeViewCatalog = await EntityTypeViewCatalog.CreateAsync(broker);
        var factory = new FieldEditorFactory(broker, entityTypeViewCatalog);

        using var document = JsonDocument.Parse(
            """
            {
              "entity-id": "c0d1e2f3-7a8b-4c9d-9e0f-6a7b8c9d0e1f",
              "entity-types": ["entity", "note"],
              "names": [["views", "sessions", "notes", "agent-manifests"]],
              "display-name": { "default": "Agent Manifests" },
              "content": {
                "default": {
                  "mime-type": "text/markdown",
                  "content": { "text": "# Agent Manifests\n\nBody text here." }
                }
              }
            }
            """);

        var fieldEditors = await factory.BuildFieldEditorsAsync(document.RootElement.Clone(), "note");

        var contentEditor = fieldEditors.Single(editor => editor.FieldName == "content");
        var localizedEditor = Assert.IsType<LocalizedMimeAttachmentFieldEditorViewModel>(contentEditor);
        var markdownEditor = Assert.IsType<MarkdownMimeAttachmentFieldEditorViewModel>(localizedEditor.ActiveEditor);
        Assert.True(markdownEditor.IsReadMode);
        Assert.True(markdownEditor.ShowMarkdownReadMode);
        Assert.Contains("Body text here.", markdownEditor.TextContent, StringComparison.Ordinal);
    }
}
