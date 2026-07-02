using System.Collections.Specialized;
using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class EntityBrowserWorkspaceTabViewModelTests
{
    [PhantomAvaloniaFact]
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
                  "entity-types": ["entity", "folder"],
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
                  "entity-types": ["entity", "entity-type"],
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
        var childItem = await WaitForItemAsync(
            viewModel,
            item => string.Equals(item.ItemKey, "[\"entity-types\",\"workspace\"]", StringComparison.Ordinal));
        Assert.Equal(2, childItem.Level);
        Assert.Equal(parentItem.ItemKey, childItem.ParentItemKey);
        Assert.Equal(EntityCardViewResolver.RawViewName, childItem.Node.Card.CardViewName);

        Assert.Equal(0, rootItem.StickyRow);
        Assert.Equal(1, parentItem.StickyRow);
        Assert.Null(childItem.StickyRow);
    }

    [PhantomAvaloniaFact]
    public async Task BrowserList_FolderItem_ExpandsViaItemToggleCommand()
    {
        var broker = await CreateBrokerAsync();

        await SeedSnapshotAsync(
            broker,
            CreateSnapshot(
                new EntityId("44444444-4444-4444-4444-444444444444"),
                new Timestamp(DateTimeOffset.UtcNow.AddMinutes(-2), "1"),
                """
                {
                  "entity-id": "44444444-4444-4444-4444-444444444444",
                  "entity-types": ["entity", "folder"],
                  "names": [["tools"]],
                  "display-name": { "default": "Tools" }
                }
                """));
        await SeedSnapshotAsync(
            broker,
            CreateSnapshot(
                new EntityId("55555555-5555-5555-5555-555555555555"),
                new Timestamp(DateTimeOffset.UtcNow.AddMinutes(-1), "1"),
                """
                {
                  "entity-id": "55555555-5555-5555-5555-555555555555",
                  "entity-types": ["entity", "tool"],
                  "names": [["tools", "git-workspace-scan"]],
                  "display-name": { "default": "Git Workspace Scan" }
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
            Id = "entity-browser-tab-folder",
            Title = "Entity Browser",
        };

        // The folder must report it has children (so the expand affordance is shown) even though it
        // is collapsed by default.
        var folderItem = await WaitForItemAsync(
            viewModel,
            item => string.Equals(item.ItemKey, "[\"tools\"]", StringComparison.Ordinal)
                && item.HasChildren);
        Assert.False(folderItem.IsExpanded);
        Assert.True(folderItem.ToggleExpandCommand.CanExecute(null));

        // Toggling via the item's command (the path the browser template binds) must expand it and
        // reveal the child. Previously the expander toggled the node, whose expansion state the
        // browser ignores on rebuild, so folders never expanded.
        folderItem.ToggleExpandCommand.Execute(null);

        var childItem = await WaitForItemAsync(
            viewModel,
            item => string.Equals(item.ItemKey, "[\"tools\",\"git-workspace-scan\"]", StringComparison.Ordinal));
        Assert.Equal(folderItem.ItemKey, childItem.ParentItemKey);
    }

    [PhantomAvaloniaFact]
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
                  "entity-types": ["entity", "note"],
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

        var noteItem = await WaitForItemAsync(
            viewModel,
            item => string.Equals(item.ItemKey, "[\"documentation\",\"markdown-note\"]", StringComparison.Ordinal)
                && item.FieldEditors.Any(static fieldEditor => fieldEditor.FieldName == "content"));
        var contentField = Assert.Single(noteItem.FieldEditors, static fieldEditor => fieldEditor.FieldName == "content");
        var localizedEditor = Assert.IsType<LocalizedMimeAttachmentFieldEditorViewModel>(contentField);
        var markdownEditor = Assert.IsType<MarkdownMimeAttachmentFieldEditorViewModel>(localizedEditor.ActiveEditor);
        Assert.Equal("# Heading\n\nBody", markdownEditor.TextContent);
    }

    [PhantomAvaloniaFact]
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
                  "entity-types": ["entity", "note"],
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

        var noteItem = await WaitForItemAsync(
            viewModel,
            item => (string.Equals(item.DisplayName, "Localized Mime Note", StringComparison.Ordinal)
                    || string.Equals(item.ItemKey, "[\"notes\",\"localized-mime\"]", StringComparison.Ordinal))
                && item.FieldEditors.Any(static fieldEditor => fieldEditor.FieldName == "content"));
        var contentField = Assert.Single(noteItem.FieldEditors, static fieldEditor => fieldEditor.FieldName == "content");
        var localizedEditor = Assert.IsType<LocalizedMimeAttachmentFieldEditorViewModel>(contentField);
        var markdownEditor = Assert.IsType<MarkdownMimeAttachmentFieldEditorViewModel>(localizedEditor.ActiveEditor);
        Assert.Equal("# Heading\n\nBody", markdownEditor.TextContent);
    }

    [PhantomAvaloniaFact]
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
                  "entity-types": ["entity", "entity-type", "json-schema"],
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

        var noteTypeItem = await WaitForItemAsync(
            viewModel,
            item => item.ItemKey.StartsWith("[\"entity-types\",", StringComparison.Ordinal)
                && item.FieldEditors.Any(fieldEditor => fieldEditor.FieldName == "schema"));
        var schemaField = Assert.Single(noteTypeItem.FieldEditors, static fieldEditor => fieldEditor.FieldName == "schema");
        var schemaEditor = Assert.IsType<JsonSchemaFieldEditorViewModel>(schemaField);
        Assert.Contains("\"properties\"", schemaEditor.JsonText, StringComparison.Ordinal);
    }

    // Regression test for #644: when many SubscribeChildPathAsync completions fire concurrent
    // RebuildTreeAsync() calls, the coalescing loop must ensure all entities become visible
    // without a test-host timeout caused by N×M parallel rebuilds.
    [PhantomAvaloniaFact]
    public async Task BrowserList_CoalescesRebuildRequests_WhenManySubscriptionsComplete()
    {
        var broker = await CreateBrokerAsync();

        // Seed several entities at distinct paths so that BuildChildrenAsync triggers a
        // SubscribeChildPathAsync call for each, causing multiple concurrent RebuildTreeAsync()
        // fire-and-forget calls during the initial build.
        for (int i = 1; i <= 6; i++)
        {
            var id = new EntityId($"{i:D8}-{i:D4}-{i:D4}-{i:D4}-{i:D12}");
            await SeedSnapshotAsync(
                broker,
                CreateSnapshot(
                    id,
                    new Timestamp(DateTimeOffset.UtcNow, i.ToString()),
                    $$"""
                    {
                      "entity-id": "{{id}}",
                      "entity-types": ["entity", "folder"],
                      "names": [["folder-{{i}}"]]
                    }
                    """));
        }

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
            Id = "entity-browser-tab-coalesce",
            Title = "Entity Browser",
        };

        // All six folders must become visible; concurrent rebuild coalescing ensures this
        // completes without the test-host timeout that the pre-fix N×M cascade caused.
        for (int i = 1; i <= 6; i++)
        {
            var folderKey = $"[\"folder-{i}\"]";
            await WaitForItemAsync(
                viewModel,
                item => string.Equals(item.ItemKey, folderKey, StringComparison.Ordinal));
        }
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

    // The entity list is rebuilt (Clear + re-add of freshly created item view models) on every
    // subscription delivery and expansion change, so re-querying viewModel.EntityList.Items with
    // Assert.Single after awaiting a condition is racy: a rebuild can momentarily drop the item in
    // the gap between the await resuming and the assertion running. Capture the matching item while
    // the condition holds (inside the CollectionChanged handler) and assert against that instance.
    // The captured item's identity-, structure-, and field-editor data are stable even after the
    // live collection is rebuilt, so the assertions no longer depend on collection timing.
    private static async Task<EntityListItemViewModel> WaitForItemAsync(
        EntityBrowserWorkspaceTabViewModel viewModel,
        Func<EntityListItemViewModel, bool> predicate)
    {
        var existing = viewModel.EntityList.Items.FirstOrDefault(predicate);
        if (existing is not null)
        {
            return existing;
        }

        var signal = new TaskCompletionSource<EntityListItemViewModel>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            var match = viewModel.EntityList.Items.FirstOrDefault(predicate);
            if (match is not null)
            {
                signal.TrySetResult(match);
            }
        }

        viewModel.EntityList.Items.CollectionChanged += OnCollectionChanged;
        try
        {
            var match = viewModel.EntityList.Items.FirstOrDefault(predicate);
            if (match is not null)
            {
                return match;
            }

            return await signal.Task;
        }
        finally
        {
            viewModel.EntityList.Items.CollectionChanged -= OnCollectionChanged;
        }
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
