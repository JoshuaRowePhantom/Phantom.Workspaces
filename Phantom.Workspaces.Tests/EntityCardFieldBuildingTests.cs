using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using System.Linq;
using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Gui.Shared.Controls;
using Phantom.Workspaces.Templates;
using Phantom.Workspaces.ViewModels;

using Phantom.Workspaces.Testing.Gui;

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

    [AvaloniaFact]
    public async Task MarkdownMimeAttachment_ReadMode_RendersMarkdownNotRawSource()
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

        var templates = new WorkspaceDataTemplates();
        var template = templates.Cast<IDataTemplate>().First(t => t.Match(markdownEditor));
        var control = template.Build(markdownEditor);
        control!.DataContext = markdownEditor;
        Dispatcher.UIThread.RunJobs();

        var views = control.GetSelfAndLogicalDescendants()
            .OfType<WorkspaceMarkdownView>()
            .ToList();

        Assert.NotEmpty(views);
        Assert.All(views, view => Assert.Equal(markdownEditor.TextContent, view.Markdown));
    }

    [AvaloniaFact]
    public async Task FieldEditorFactory_RendersNoteContentInline()
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

        // The note's content view marks the field "inline", so read mode shows only the rendered markdown
        // (no expander, mime-type, url, or content.text chrome).
        Assert.True(markdownEditor.IsInline);
        Assert.True(markdownEditor.ShowInlineMarkdownReadMode);
        Assert.False(markdownEditor.ShowChrome);
        Assert.True(localizedEditor.ShowInlineReadMode);
        Assert.False(localizedEditor.ShowChrome);

        // Entering edit mode restores the full editing chrome.
        markdownEditor.IsEditMode = true;
        Assert.False(markdownEditor.ShowInlineMarkdownReadMode);
        Assert.True(markdownEditor.ShowChrome);
    }

    [AvaloniaFact]
    public async Task FieldEditorFactory_RendersNoFields_WhenEntityTypeViewHasEmptyFields()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);
        var entityTypeViewCatalog = new EntityTypeViewCatalog(
            [new EntityTypeViewDefinition("agent-manifest", null, System.Array.Empty<EntityFieldViewDefinition>())]);
        var factory = new FieldEditorFactory(broker, entityTypeViewCatalog);

        using var document = JsonDocument.Parse(
            """
            {
              "entity-id": "b9c0d1e2-6f7a-4b8c-9d0e-5f6a7b8c9d0e",
              "entity-types": ["entity", "agent-manifest"],
              "names": [["agent-manifests", "example"]],
              "display-name": { "default": "Example Manifest" },
              "manifest": { "template": { "display-name": "Example" } }
            }
            """);

        var fieldEditors = await factory.BuildFieldEditorsAsync(document.RootElement.Clone(), "agent-manifest");

        Assert.Empty(fieldEditors);
    }

    [AvaloniaFact]
    public async Task FieldEditorFactory_RendersAllFields_WhenEntityTypeViewOmitsFields()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);
        var entityTypeViewCatalog = new EntityTypeViewCatalog(
            [new EntityTypeViewDefinition("agent-manifest", null, Fields: null)]);
        var factory = new FieldEditorFactory(broker, entityTypeViewCatalog);

        using var document = JsonDocument.Parse(
            """
            {
              "entity-id": "b9c0d1e2-6f7a-4b8c-9d0e-5f6a7b8c9d0e",
              "entity-types": ["entity", "agent-manifest"],
              "names": [["agent-manifests", "example"]],
              "display-name": { "default": "Example Manifest" },
              "manifest": { "template": { "display-name": "Example" } }
            }
            """);

        var fieldEditors = await factory.BuildFieldEditorsAsync(document.RootElement.Clone(), "agent-manifest");

        Assert.NotEmpty(fieldEditors);
    }

    [AvaloniaFact]
    public async Task FieldEditorFactory_RendersNoFields_WhenNoEntityTypeViewRegistered()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);
        // Empty catalog — no view registered for the entity type.
        var entityTypeViewCatalog = new EntityTypeViewCatalog([]);
        var factory = new FieldEditorFactory(broker, entityTypeViewCatalog);

        using var document = JsonDocument.Parse(
            """
            {
              "entity-id": "b9c0d1e2-6f7a-4b8c-9d0e-5f6a7b8c9d0f",
              "entity-types": ["entity", "agent-definition"],
              "names": [["agent-definitions", "example"]],
              "display-name": { "default": "Example Agent" },
              "template": { "display-name": "Example" }
            }
            """);

        var fieldEditors = await factory.BuildFieldEditorsAsync(document.RootElement.Clone(), "agent-definition");

        Assert.Empty(fieldEditors);
    }

    [AvaloniaFact]
    public async Task FieldEditorFactory_BuildsEntityListEditorForEntityIdListFields()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);
        var entityTypeViewCatalog = await EntityTypeViewCatalog.CreateAsync(broker);
        var factory = new FieldEditorFactory(broker, entityTypeViewCatalog);

        using var document = JsonDocument.Parse(
            """
            {
              "entity-id": "d1e2f3a4-5b6c-4d7e-8f9a-0b1c2d3e4f5a",
              "entity-types": ["entity", "relationship", "tool-relationship"],
              "names": [["tool-relationships", "example"]],
              "participants": {
                "tool": "11111111-1111-1111-1111-111111111111",
                "schedule": ["22222222-2222-2222-2222-222222222222"],
                "target": [
                  "33333333-3333-3333-3333-333333333333",
                  "44444444-4444-4444-4444-444444444444"
                ]
              }
            }
            """);

        var fieldEditors = await factory.BuildFieldEditorsAsync(document.RootElement.Clone(), "tool-relationship");

        var scheduleEditor = Assert.IsType<EntityListFieldEditorViewModel>(
            fieldEditors.Single(editor => editor.FieldName == "schedule"));
        Assert.Single(scheduleEditor.Items);

        var targetEditor = Assert.IsType<EntityListFieldEditorViewModel>(
            fieldEditors.Single(editor => editor.FieldName == "target"));
        Assert.Equal(2, targetEditor.Items.Count);

        // A singleton entity-id field still resolves to the entity-reference editor.
        Assert.IsType<EntityReferenceFieldEditorViewModel>(
            fieldEditors.Single(editor => editor.FieldName == "tool"));
    }

    [AvaloniaFact]
    public async Task FieldEditorFactory_ResolvesEntityIdListIndependentOfEntityTypeOrder()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);
        var entityTypeViewCatalog = await EntityTypeViewCatalog.CreateAsync(broker);
        var factory = new FieldEditorFactory(broker, entityTypeViewCatalog);

        // The derived "tool-relationship" type is listed BEFORE the base "relationship" type.
        // Field-type resolution must pick the most-specific schema (tool-relationship's direct
        // `participants.properties.schedule` entity-id-list) rather than the first schema whose
        // `participants.additionalProperties` fallback happens to match, regardless of order.
        using var document = JsonDocument.Parse(
            """
            {
              "entity-id": "d1e2f3a4-5b6c-4d7e-8f9a-0b1c2d3e4f5b",
              "entity-types": ["tool-relationship", "relationship", "entity"],
              "names": [["tool-relationships", "reordered"]],
              "participants": {
                "tool": "11111111-1111-1111-1111-111111111111",
                "schedule": ["22222222-2222-2222-2222-222222222222"],
                "target": [
                  "33333333-3333-3333-3333-333333333333",
                  "44444444-4444-4444-4444-444444444444"
                ]
              }
            }
            """);

        var fieldEditors = await factory.BuildFieldEditorsAsync(document.RootElement.Clone(), "tool-relationship");

        Assert.IsType<EntityListFieldEditorViewModel>(
            fieldEditors.Single(editor => editor.FieldName == "schedule"));
        Assert.IsType<EntityListFieldEditorViewModel>(
            fieldEditors.Single(editor => editor.FieldName == "target"));
        Assert.IsType<EntityReferenceFieldEditorViewModel>(
            fieldEditors.Single(editor => editor.FieldName == "tool"));
    }

    [AvaloniaFact]
    public async Task FieldEditorFactory_BuildsBooleanToggleEditorForPausedField()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);
        var entityTypeViewCatalog = await EntityTypeViewCatalog.CreateAsync(broker);
        var factory = new FieldEditorFactory(broker, entityTypeViewCatalog);

        using var document = JsonDocument.Parse(
            """
            {
              "entity-id": "d1e2f3a4-5b6c-4d7e-8f9a-0b1c2d3e4f5c",
              "entity-types": ["entity", "relationship", "tool-relationship"],
              "names": [["tool-relationships", "paused-example"]],
              "participants": {
                "tool": "11111111-1111-1111-1111-111111111111",
                "schedule": ["22222222-2222-2222-2222-222222222222"],
                "target": ["33333333-3333-3333-3333-333333333333"]
              },
              "paused": true,
              "last-started": "2026-06-17T09:30:00Z"
            }
            """);

        var fieldEditors = await factory.BuildFieldEditorsAsync(document.RootElement.Clone(), "tool-relationship");

        var pausedEditor = Assert.IsType<BooleanToggleFieldEditorViewModel>(
            fieldEditors.Single(editor => editor.FieldName == "paused"));
        Assert.True(pausedEditor.Value);

        // The relationship view also surfaces last-started for inspection.
        Assert.Contains(fieldEditors, editor => editor.FieldName == "last-started");
    }

    [AvaloniaFact]
    public async Task FieldEditorFactory_BuildsBooleanToggleEditor_DefaultsFalse_WhenPausedAbsent()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);
        var entityTypeViewCatalog = await EntityTypeViewCatalog.CreateAsync(broker);
        var factory = new FieldEditorFactory(broker, entityTypeViewCatalog);

        using var document = JsonDocument.Parse(
            """
            {
              "entity-id": "d1e2f3a4-5b6c-4d7e-8f9a-0b1c2d3e4f5d",
              "entity-types": ["entity", "relationship", "tool-relationship"],
              "names": [["tool-relationships", "unpaused-example"]],
              "participants": {
                "tool": "11111111-1111-1111-1111-111111111111",
                "schedule": ["22222222-2222-2222-2222-222222222222"],
                "target": ["33333333-3333-3333-3333-333333333333"]
              }
            }
            """);

        var fieldEditors = await factory.BuildFieldEditorsAsync(document.RootElement.Clone(), "tool-relationship");

        var pausedEditor = Assert.IsType<BooleanToggleFieldEditorViewModel>(
            fieldEditors.Single(editor => editor.FieldName == "paused"));
        Assert.False(pausedEditor.Value);
    }

    // ---- Issue #1164: multi-typed entity card composition ------------------------------------

    [AvaloniaFact]
    public async Task FieldEditorFactory_ToolAndNoteEntityTypes_ComposesFieldsFromAllTypeViews()
    {
        // A tool+note entity (e.g. seeded "Run VS Code Tunnel") declares content.default markdown.
        // The tool entity-type has no registered view, but the note view surfaces the `content`
        // field. Composing across both types must still emit the note-contributed `content` editor.
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);
        var entityTypeViewCatalog = new EntityTypeViewCatalog(
            [new EntityTypeViewDefinition(
                "note",
                null,
                new[] { new EntityFieldViewDefinition(new[] { "content" }, "inline") })]);
        var factory = new FieldEditorFactory(broker, entityTypeViewCatalog);

        using var document = JsonDocument.Parse(
            """
            {
              "entity-id": "e4f5a6b7-c8d9-4e0f-b1c2-d3e4f5a6b7c8",
              "entity-types": ["entity", "tool", "note"],
              "names": [["tools", "run-vs-code-tunnel"]],
              "display-name": { "default": "Run VS Code Tunnel" },
              "content": {
                "default": {
                  "mime-type": "text/markdown",
                  "content": { "text": "# Run VS Code Tunnel\n\nUsage." }
                }
              }
            }
            """);

        var fieldEditors = await factory.BuildFieldEditorsAsync(
            document.RootElement.Clone(),
            new[] { "tool", "note" });

        Assert.Contains(fieldEditors, editor => editor.FieldName == "content");
    }

    [AvaloniaFact]
    public async Task FieldEditorFactory_MultipleEntityTypes_DeduplicatesFieldPaths()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);
        var entityTypeViewCatalog = new EntityTypeViewCatalog(new[]
        {
            new EntityTypeViewDefinition(
                "tool",
                null,
                new[] { new EntityFieldViewDefinition(new[] { "content" }, null) }),
            new EntityTypeViewDefinition(
                "note",
                null,
                new[] { new EntityFieldViewDefinition(new[] { "content" }, "inline") }),
        });
        var factory = new FieldEditorFactory(broker, entityTypeViewCatalog);

        using var document = JsonDocument.Parse(
            """
            {
              "entity-id": "e4f5a6b7-c8d9-4e0f-b1c2-d3e4f5a6b7c8",
              "entity-types": ["entity", "tool", "note"],
              "names": [["tools", "run-vs-code-tunnel"]],
              "display-name": { "default": "Run VS Code Tunnel" },
              "content": {
                "default": {
                  "mime-type": "text/markdown",
                  "content": { "text": "# Body" }
                }
              }
            }
            """);

        var fieldEditors = await factory.BuildFieldEditorsAsync(
            document.RootElement.Clone(),
            new[] { "tool", "note" });

        Assert.Single(fieldEditors, editor => editor.FieldName == "content");
    }

    [AvaloniaFact]
    public async Task FieldEditorFactory_MultipleEntityTypes_OrdersFieldsByContributingTypeEntityDisplayOrder()
    {
        // The tool entity-type has entity-display-order: 260 while note has none (defaults last),
        // so a tool-contributed field must render before a note-contributed field.
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);
        var entityTypeViewCatalog = new EntityTypeViewCatalog(new[]
        {
            new EntityTypeViewDefinition(
                "tool",
                null,
                new[] { new EntityFieldViewDefinition(new[] { "display-name" }, null) }),
            new EntityTypeViewDefinition(
                "note",
                null,
                new[] { new EntityFieldViewDefinition(new[] { "content" }, "inline") }),
        });
        var factory = new FieldEditorFactory(broker, entityTypeViewCatalog);

        using var document = JsonDocument.Parse(
            """
            {
              "entity-id": "e4f5a6b7-c8d9-4e0f-b1c2-d3e4f5a6b7c8",
              "entity-types": ["entity", "tool", "note"],
              "names": [["tools", "run-vs-code-tunnel"]],
              "display-name": { "default": "Run VS Code Tunnel" },
              "content": {
                "default": {
                  "mime-type": "text/markdown",
                  "content": { "text": "# Body" }
                }
              }
            }
            """);

        var fieldEditors = (await factory.BuildFieldEditorsAsync(
            document.RootElement.Clone(),
            new[] { "tool", "note" })).ToArray();

        var displayNameIndex = System.Array.FindIndex(fieldEditors, e => e.FieldName == "display-name");
        var contentIndex = System.Array.FindIndex(fieldEditors, e => e.FieldName == "content");
        Assert.True(displayNameIndex >= 0);
        Assert.True(contentIndex >= 0);
        Assert.True(displayNameIndex < contentIndex,
            $"tool-contributed display-name (index {displayNameIndex}) must render before note-contributed content (index {contentIndex}).");
    }

    [AvaloniaFact]
    public async Task FieldEditorFactory_MultipleEntityTypes_PreservesEntityTypeViewFieldsArrayOrderWithinAType()
    {
        // A single entity-type-view.fields array [A, B, C] with no explicit x-*-entity-display-order
        // keeps its declaration order across the composed output.
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);
        var entityTypeViewCatalog = new EntityTypeViewCatalog(new[]
        {
            new EntityTypeViewDefinition(
                "note",
                null,
                new[]
                {
                    new EntityFieldViewDefinition(new[] { "display-name" }, null),
                    new EntityFieldViewDefinition(new[] { "title" }, null),
                    new EntityFieldViewDefinition(new[] { "content" }, "inline"),
                }),
        });
        var factory = new FieldEditorFactory(broker, entityTypeViewCatalog);

        using var document = JsonDocument.Parse(
            """
            {
              "entity-id": "aaaaaaaa-1111-2222-3333-444444444444",
              "entity-types": ["entity", "note"],
              "names": [["notes", "ordered"]],
              "display-name": { "default": "Ordered" },
              "title": { "default": "T" },
              "content": {
                "default": { "mime-type": "text/markdown", "content": { "text": "# Body" } }
              }
            }
            """);

        var fieldEditors = (await factory.BuildFieldEditorsAsync(
            document.RootElement.Clone(),
            new[] { "note" })).ToArray();

        var names = fieldEditors.Select(e => e.FieldName).ToArray();
        Assert.Equal(new[] { "display-name", "title", "content" }, names);
    }

    [AvaloniaFact]
    public void FieldEditorFactory_MultipleEntityTypes_AbsoluteEntityDisplayOrderStillWinsAcrossTypes()
    {
        // Preserve the existing FieldOrderingKey group-0 behavior in the multi-type case: a field
        // whose schema declares x-absolute-entity-display-order sorts strictly by that value into
        // group 0, ahead of every type-grouped field, regardless of which contributing entity type
        // owns it. This is what allows note.content to render above tool-grouped fields when the
        // note schema tags it absolute-ordered.
        var noteContributedAbsolute = FieldOrdering.ComputeKey(
            fieldName: "content",
            absoluteOrder: 5,
            relativeOrder: 0,
            entityTypeName: "note",
            entityTypeDisplayOrder: null);
        var toolContributedGrouped = FieldOrdering.ComputeKey(
            fieldName: "display-name",
            absoluteOrder: null,
            relativeOrder: 0,
            entityTypeName: "tool",
            entityTypeDisplayOrder: 260);
        var noteContributedGrouped = FieldOrdering.ComputeKey(
            fieldName: "title",
            absoluteOrder: null,
            relativeOrder: 0,
            entityTypeName: "note",
            entityTypeDisplayOrder: null);

        Assert.True(noteContributedAbsolute.CompareTo(toolContributedGrouped) < 0);
        Assert.True(noteContributedAbsolute.CompareTo(noteContributedGrouped) < 0);
        // Sanity check the group-1 tool-vs-note tool-before-note ordering used elsewhere.
        Assert.True(toolContributedGrouped.CompareTo(noteContributedGrouped) < 0);
    }

    [AvaloniaFact]
    public async Task FieldEditorFactory_NoEntityTypeViewsForAnyType_ReturnsEmpty()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);
        var entityTypeViewCatalog = new EntityTypeViewCatalog([]);
        var factory = new FieldEditorFactory(broker, entityTypeViewCatalog);

        using var document = JsonDocument.Parse(
            """
            {
              "entity-id": "cccccccc-3333-4444-5555-666666666666",
              "entity-types": ["entity", "tool", "note"],
              "names": [["tools", "nothing"]],
              "display-name": { "default": "Nothing" }
            }
            """);

        var fieldEditors = await factory.BuildFieldEditorsAsync(
            document.RootElement.Clone(),
            new[] { "tool", "note" });

        Assert.Empty(fieldEditors);
    }
}
