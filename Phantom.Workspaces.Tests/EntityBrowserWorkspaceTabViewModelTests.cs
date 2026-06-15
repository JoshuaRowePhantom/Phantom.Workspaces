using System.Collections.Specialized;
using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class EntityBrowserWorkspaceTabViewModelTests
{
    [AvaloniaFact]
    public async Task BrowserList_TracksParentChildMetadataAndExpansion()
    {
        var broker = await CreateBrokerAsync();

        var parentId = new EntityId("11111111-1111-1111-1111-111111111111");
        var childId = new EntityId("22222222-2222-2222-2222-222222222222");
        await SeedSnapshotAsync(
            broker,
            CreateSnapshot(
                parentId,
                new Timestamp(DateTimeOffset.UtcNow.AddMinutes(-2), "1"),
                """
                {
                  "entity-id": "11111111-1111-1111-1111-111111111111",
                  "entity-types": ["folder"],
                  "names": [["entity-types"]],
                  "display-name": { "default": "Entity Types" }
                }
                """));
        await SeedSnapshotAsync(
            broker,
            CreateSnapshot(
                childId,
                new Timestamp(DateTimeOffset.UtcNow.AddMinutes(-1), "1"),
                """
                {
                  "entity-id": "22222222-2222-2222-2222-222222222222",
                  "entity-types": ["entity-type"],
                  "names": [["entity-types", "workspace"]],
                  "display-name": { "default": "Workspace" }
                }
                """));

        var rootSubscription = await broker.SubscribeGetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityName = EntityName.Root,
                        EnumerateChildren = EnumerateChildrenAction.EnumerateSelf,
                    },
                    new GetEntityRequest
                    {
                        EntityName = EntityName.Root,
                        EnumerateChildren = EnumerateChildrenAction.EnumerateChildren,
                    },
                ],
                Timestamps = [null],
            },
            TestContext.Current.CancellationToken);
        var viewModel = new EntityBrowserWorkspaceTabViewModel(broker, rootSubscription)
        {
            Id = "entity-browser-tab",
            Title = "Entity Browser",
        };

        await WaitForConditionAsync(viewModel, () =>
            viewModel.EntityList.Items.Any(item =>
                string.Equals(item.ItemKey, "[\"entity-types\"]", StringComparison.Ordinal)
                && item.HasChildren));

        var rootItem = Assert.Single(
            viewModel.EntityList.Items,
            item => string.Equals(item.ItemKey, "[]", StringComparison.Ordinal));
        Assert.True(rootItem.IsExpanded);
        Assert.Equal(0, rootItem.Level);
        Assert.Null(rootItem.ParentItemKey);

        var parentItem = Assert.Single(
            viewModel.EntityList.Items,
            item => string.Equals(item.ItemKey, "[\"entity-types\"]", StringComparison.Ordinal));
        Assert.Equal("[\"entity-types\"]", parentItem.ItemKey);
        Assert.Equal("[]", parentItem.ParentItemKey);
        Assert.Equal(1, parentItem.Level);
        Assert.Contains("[\"entity-types\",\"workspace\"]", parentItem.ChildItemKeys);

        parentItem.IsExpanded = true;
        await WaitForConditionAsync(viewModel, () =>
            viewModel.EntityList.Items.Any(item =>
                string.Equals(item.ItemKey, "[\"entity-types\",\"workspace\"]", StringComparison.Ordinal)));

        var childItem = Assert.Single(
            viewModel.EntityList.Items,
            item => string.Equals(item.ItemKey, "[\"entity-types\",\"workspace\"]", StringComparison.Ordinal));
        Assert.Equal(2, childItem.Level);
        Assert.Equal(parentItem.ItemKey, childItem.ParentItemKey);
        Assert.Equal(EntityCardViewResolver.RawViewName, childItem.Node.CardViewName);

        Assert.Equal(0, rootItem.StickyRow);
        Assert.Equal(1, parentItem.StickyRow);
        Assert.Null(childItem.StickyRow);
    }

    [AvaloniaFact]
    public async Task BrowserList_UsesMarkdownMimeEditor_WhenValueShapeIsMimeAttachment()
    {
        var broker = await CreateBrokerAsync();
        var noteId = new EntityId("33333333-3333-3333-3333-333333333333");
        await SeedSnapshotAsync(
            broker,
            CreateSnapshot(
                noteId,
                new Timestamp(DateTimeOffset.UtcNow, "1"),
                """
                {
                  "entity-id": "33333333-3333-3333-3333-333333333333",
                  "entity-types": ["note"],
                  "names": [["documentation", "markdown-note"]],
                  "display-name": { "default": "Markdown Note" },
                  "content": {
                    "mime-type": "text/markdown",
                    "url": "documentation/getting-started.md",
                    "content": {
                      "text": "# Heading\n\nBody"
                    }
                  }
                }
                """));

        var rootSubscription = await broker.SubscribeGetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityName = EntityName.Root,
                        EnumerateChildren = EnumerateChildrenAction.EnumerateSelf,
                    },
                    new GetEntityRequest
                    {
                        EntityName = EntityName.Root,
                        EnumerateChildren = EnumerateChildrenAction.EnumerateChildren,
                    },
                ],
                Timestamps = [null],
            },
            TestContext.Current.CancellationToken);
        var viewModel = new EntityBrowserWorkspaceTabViewModel(broker, rootSubscription)
        {
            Id = "entity-browser-tab-markdown",
            Title = "Entity Browser",
        };

        await WaitForConditionAsync(viewModel, () =>
            viewModel.EntityList.Items.Any(item =>
                string.Equals(item.ItemKey, "[\"documentation\"]", StringComparison.Ordinal)));

        var documentationItem = Assert.Single(
            viewModel.EntityList.Items,
            item => string.Equals(item.ItemKey, "[\"documentation\"]", StringComparison.Ordinal));
        documentationItem.IsExpanded = true;

        await WaitForConditionAsync(viewModel, () =>
            viewModel.EntityList.Items.Any(item =>
                string.Equals(item.ItemKey, "[\"documentation\",\"markdown-note\"]", StringComparison.Ordinal)));

        var noteItem = Assert.Single(
            viewModel.EntityList.Items,
            item => string.Equals(item.ItemKey, "[\"documentation\",\"markdown-note\"]", StringComparison.Ordinal));
        var contentField = Assert.Single(noteItem.FieldEditors, static fieldEditor => fieldEditor.FieldName == "content");
        var localizedEditor = Assert.IsType<LocalizedMimeAttachmentFieldEditorViewModel>(contentField);
        var markdownEditor = Assert.IsType<MarkdownMimeAttachmentFieldEditorViewModel>(localizedEditor.ActiveEditor);
        Assert.Equal("# Heading\n\nBody", markdownEditor.TextContent);
    }

    [AvaloniaFact]
    public async Task BrowserList_UsesMarkdownMimeEditor_WhenValueShapeIsLocalizedMimeAttachment()
    {
        var broker = await CreateBrokerAsync();
        var noteEntityId = new EntityId("33333333-3333-3333-3333-333333333333");
        await SeedSnapshotAsync(
            broker,
            CreateSnapshot(
                noteEntityId,
                new Timestamp(DateTimeOffset.UtcNow, "1"),
                """
                {
                  "entity-id": "33333333-3333-3333-3333-333333333333",
                  "entity-types": ["note"],
                  "names": [["notes", "localized-mime"]],
                  "display-name": { "default": "Localized Mime" },
                  "content": {
                    "default": {
                      "mime-type": "text/markdown",
                      "content": { "text": "# Heading\n\nBody" }
                    }
                  }
                }
                """));

        var rootSubscription = await broker.SubscribeGetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityName = EntityName.Root,
                        EnumerateChildren = EnumerateChildrenAction.EnumerateSelf,
                    },
                    new GetEntityRequest
                    {
                        EntityName = EntityName.Root,
                        EnumerateChildren = EnumerateChildrenAction.EnumerateChildren,
                    },
                ],
                Timestamps = [null],
            },
            TestContext.Current.CancellationToken);
        var viewModel = new EntityBrowserWorkspaceTabViewModel(broker, rootSubscription)
        {
            Id = "entity-browser-tab",
            Title = "Entity Browser",
        };

        await WaitForConditionAsync(viewModel, () =>
            viewModel.EntityList.Items.Any(item =>
                string.Equals(item.ItemKey, "[\"notes\"]", StringComparison.Ordinal)));

        var notesItem = Assert.Single(
            viewModel.EntityList.Items,
            item => string.Equals(item.ItemKey, "[\"notes\"]", StringComparison.Ordinal));
        notesItem.IsExpanded = true;

        await WaitForConditionAsync(viewModel, () =>
            viewModel.EntityList.Items.Any(item =>
                string.Equals(item.DisplayName, "Localized Mime Note", StringComparison.Ordinal)
                || string.Equals(item.ItemKey, "[\"notes\",\"localized-mime\"]", StringComparison.Ordinal)));

        var noteItem = Assert.Single(
            viewModel.EntityList.Items,
            item => string.Equals(item.DisplayName, "Localized Mime Note", StringComparison.Ordinal)
                || string.Equals(item.ItemKey, "[\"notes\",\"localized-mime\"]", StringComparison.Ordinal));
        var contentField = Assert.Single(noteItem.FieldEditors, static fieldEditor => fieldEditor.FieldName == "content");
        var localizedEditor = Assert.IsType<LocalizedMimeAttachmentFieldEditorViewModel>(contentField);
        var markdownEditor = Assert.IsType<MarkdownMimeAttachmentFieldEditorViewModel>(localizedEditor.ActiveEditor);
        Assert.Equal("# Heading\n\nBody", markdownEditor.TextContent);
    }

    [AvaloniaFact]
    public async Task BrowserList_UsesJsonSchemaFieldEditor_ForSchemaField()
    {
        var broker = await CreateBrokerAsync();
        var schemaEntityId = new EntityId("44444444-4444-4444-4444-444444444444");
        await SeedSnapshotAsync(
            broker,
            CreateSnapshot(
                schemaEntityId,
                new Timestamp(DateTimeOffset.UtcNow, "1"),
                """
                {
                  "entity-id": "44444444-4444-4444-4444-444444444444",
                  "entity-types": ["entity-type", "json-schema"],
                  "names": [["entity-types", "note"]],
                  "display-name": { "default": "Note Type" },
                  "schema": {
                    "type": "object",
                    "properties": {
                      "content": { "type": "string" }
                    }
                  }
                }
                """));

        var rootSubscription = await broker.SubscribeGetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityName = EntityName.Root,
                        EnumerateChildren = EnumerateChildrenAction.EnumerateSelf,
                    },
                    new GetEntityRequest
                    {
                        EntityName = EntityName.Root,
                        EnumerateChildren = EnumerateChildrenAction.EnumerateChildren,
                    },
                ],
                Timestamps = [null],
            },
            TestContext.Current.CancellationToken);
        var viewModel = new EntityBrowserWorkspaceTabViewModel(broker, rootSubscription)
        {
            Id = "entity-browser-tab-schema",
            Title = "Entity Browser",
        };

        await WaitForConditionAsync(viewModel, () =>
            viewModel.EntityList.Items.Any(item =>
                string.Equals(item.ItemKey, "[\"entity-types\"]", StringComparison.Ordinal)));

        var entityTypesItem = Assert.Single(
            viewModel.EntityList.Items,
            item => string.Equals(item.ItemKey, "[\"entity-types\"]", StringComparison.Ordinal));
        entityTypesItem.IsExpanded = true;

        await WaitForConditionAsync(viewModel, () =>
            viewModel.EntityList.Items.Any(item =>
                item.ItemKey.StartsWith("[\"entity-types\",", StringComparison.Ordinal)
                && item.FieldEditors.Any(fieldEditor => fieldEditor.FieldName == "schema")));

        var noteTypeItem = viewModel.EntityList.Items.FirstOrDefault(item =>
            item.ItemKey.StartsWith("[\"entity-types\",", StringComparison.Ordinal)
            && item.FieldEditors.Any(fieldEditor => fieldEditor.FieldName == "schema"));
        Assert.NotNull(noteTypeItem);
        var schemaField = Assert.Single(noteTypeItem.FieldEditors, static fieldEditor => fieldEditor.FieldName == "schema");
        var schemaEditor = Assert.IsType<JsonSchemaFieldEditorViewModel>(schemaField);
        Assert.Contains("\"properties\"", schemaEditor.JsonText, StringComparison.Ordinal);
    }

    private static Task<EntityBroker> CreateBrokerAsync()
    {
        return EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);
    }

    private static async Task SeedSnapshotAsync(
        EntityBroker broker,
        EntitySnapshot snapshot,
        ConcurrencyTag? concurrencyTag = null)
    {
        await broker.EntityRepository.DataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Seed browser test snapshot.",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = snapshot.EntityId,
                        ConcurrencyTag = concurrencyTag,
                        EntityChangeMode = EntityChangeMode.Replace,
                        Data = snapshot.Data?.Clone(),
                    },
                ],
            },
            TestContext.Current.CancellationToken);
    }

    private static EntitySnapshot CreateSnapshot(
        EntityId entityId,
        Timestamp modifiedTime,
        string json)
    {
        using var document = JsonDocument.Parse(json);
        return new EntitySnapshot
        {
            EntityId = entityId,
            ConcurrencyTag = new ConcurrencyTag(modifiedTime.ChangeId),
            ModifiedTime = modifiedTime,
            Data = document.RootElement.Clone(),
            Relationships = Array.Empty<EntitySnapshot>(),
        };
    }

    private static async Task WaitForConditionAsync(
        EntityBrowserWorkspaceTabViewModel viewModel,
        Func<bool> condition)
    {
        if (condition())
        {
            return;
        }

        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NotifyCollectionChangedEventHandler? handler = null;
        handler = (_, _) =>
        {
            if (condition())
            {
                signal.TrySetResult();
            }
        };

        viewModel.EntityList.Items.CollectionChanged += handler;
        try
        {
            if (condition())
            {
                return;
            }

            await signal.Task;
        }
        finally
        {
            viewModel.EntityList.Items.CollectionChanged -= handler;
        }
    }
}
