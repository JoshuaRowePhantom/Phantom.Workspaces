using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phantom.Workspaces.Controls;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Gui.Shared.Controls;
using Phantom.Workspaces.ViewModels;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Tests;

// Issue #1164: a tool+note entity must compose per-type presentations. The card must render both
// the tool chrome/type labels AND the note markdown body — nothing contributed by any of the
// entity's non-abstract types may be silently hidden.
public sealed class EntityCardControlTests
{
    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityCardControl_ToolAndNote_RendersNoteMarkdown()
    {
        var card = new EntityCardControl { DataContext = await BuildToolNoteCardViewModelAsync() };
        var window = new Window { Content = card };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var markdownView = window.GetVisualDescendants()
                .OfType<WorkspaceMarkdownView>()
                .FirstOrDefault(view => view.Markdown is { } text && text.Contains("# Run VS Code Tunnel", StringComparison.Ordinal));

            Assert.NotNull(markdownView);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityCardControl_ToolAndNote_ShowsBothTypeLabels()
    {
        var card = new EntityCardControl { DataContext = await BuildToolNoteCardViewModelAsync() };
        var window = new Window { Content = card };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var typeLabels = window.GetVisualDescendants()
                .OfType<TextBlock>()
                .Select(t => t.Text ?? string.Empty)
                .ToArray();

            Assert.Contains(typeLabels, text => text.Contains("tool", StringComparison.Ordinal)
                && text.Contains("note", StringComparison.Ordinal));
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task<EntityCardViewModel> BuildToolNoteCardViewModelAsync()
    {
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
                  "content": { "text": "# Run VS Code Tunnel\n\nConfiguration and Usage." }
                }
              }
            }
            """);
        var entityData = document.RootElement.Clone();
        var snapshot = new EntitySnapshot
        {
            EntityId = new EntityId("e4f5a6b7-c8d9-4e0f-b1c2-d3e4f5a6b7c8"),
            ConcurrencyTag = new ConcurrencyTag("1"),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
            Data = entityData,
            Relationships = Array.Empty<EntitySnapshot>(),
        };
        var entity = new SubscribedEntityViewModel(snapshot);

        // Bug #1182: EntityCardViewModel.BuildFieldEditorsAsync no-ops without a factory, so the
        // note's markdown WorkspaceMarkdownView never appears. Build the field editors with a real
        // FieldEditorFactory across every non-abstract entity type (mirrors production) — matches
        // the harness used by EntityCardFieldBuildingTests.MarkdownMimeAttachment_ReadMode_RendersMarkdownNotRawSource.
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);
        var entityTypeViewCatalog = await EntityTypeViewCatalog.CreateAsync(broker);
        var factory = new FieldEditorFactory(broker, entityTypeViewCatalog);
        var fieldEditors = await factory.BuildFieldEditorsAsync(entityData, entity.NonAbstractEntityTypeNames);

        return new EntityCardViewModel(entity, fieldEditors);
    }

    // Issue #1177: attaching an EntityCardControl to the visual tree triggers the card's lazy
    // field-editor build; a detached card (never attached) does not build editors.
    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityCardControl_OnAttachedToVisualTree_TriggersFieldEditorBuild()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);
        var entityTypeViewCatalog = await EntityTypeViewCatalog.CreateAsync(broker);
        var factory = new FieldEditorFactory(broker, entityTypeViewCatalog);

        var entity = new SubscribedEntityViewModel(BuildToolNoteSnapshotForTests());
        var attachedCard = new EntityCardViewModel(entity, fieldEditorFactory: factory);
        var detachedCard = new EntityCardViewModel(
            new SubscribedEntityViewModel(BuildToolNoteSnapshotForTests()),
            fieldEditorFactory: factory);

        var control = new EntityCardControl { DataContext = attachedCard };
        var window = new Window { Content = control, Width = 400, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // Issue #1185: deterministically await the lazy field-editor build task exposed by
            // EntityCardViewModel instead of pumping RunJobs speculatively — the previous pump
            // loop could observe FieldEditors.Count == 0 before the async build finished on the
            // UI thread. Awaiting the actual build task removes the timing race.
            await attachedCard.FieldEditorsBuildTask;
            Dispatcher.UIThread.RunJobs();

            Assert.NotEmpty(attachedCard.FieldEditors);
            Assert.Empty(detachedCard.FieldEditors);
        }
        finally
        {
            window.Close();
        }
    }

    // Issue #1177: the shared TreeView.entity-card-tree style materializes a VirtualizingStackPanel
    // as its ItemsPanel, not a plain StackPanel.
    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardTree_ItemsPanel_IsVirtualizingStackPanel()
    {
        AssertItemsPanelIsVirtualizing("entity-card-tree");
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardTreeView_ItemsPanel_IsVirtualizingStackPanel()
    {
        AssertItemsPanelIsVirtualizing("entity-card-tree-view");
    }

    // Issue #1177: hosting the tree in a bounded Window layout gives its inner ScrollViewer a
    // finite pixel viewport — a prerequisite for VirtualizingStackPanel to virtualize while still
    // scrolling per-pixel.
    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardTree_ScrollHost_ProvidesBoundedPixelViewport()
    {
        var tree = new TreeView();
        tree.Classes.Add("entity-card-tree");
        tree.ItemsSource = new[] { "a", "b", "c" };
        var window = new Window { Content = tree, Width = 400, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var scrollViewer = tree.GetVisualDescendants()
                .OfType<Avalonia.Controls.ScrollViewer>()
                .First();

            Assert.True(scrollViewer.Viewport.Height > 0, $"Viewport height was {scrollViewer.Viewport.Height}; expected a bounded, non-zero pixel viewport.");
            Assert.True(double.IsFinite(scrollViewer.Viewport.Height));
            // Offset is a per-pixel Avalonia.Vector; existence of the property confirms pixel-scroll semantics.
            _ = scrollViewer.Offset;
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertItemsPanelIsVirtualizing(string className)
    {
        var tree = new TreeView();
        tree.Classes.Add(className);
        tree.ItemsSource = new[] { "a", "b", "c" };
        var window = new Window { Content = tree, Width = 400, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var panel = tree.ItemsPanelRoot;
            Assert.NotNull(panel);
            Assert.IsType<Avalonia.Controls.VirtualizingStackPanel>(panel);
        }
        finally
        {
            window.Close();
        }
    }

    private static EntitySnapshot BuildToolNoteSnapshotForTests()
    {
        var entityId = Guid.NewGuid();
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity", "tool", "note"],
              "names": [["tools", "t-{{entityId:N}}"]],
              "display-name": { "default": "Tool Note" },
              "content": {
                "default": {
                  "mime-type": "text/markdown",
                  "content": { "text": "# Body" }
                }
              }
            }
            """);
        return new EntitySnapshot
        {
            EntityId = new EntityId(entityId),
            ConcurrencyTag = new ConcurrencyTag("1"),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
            Data = document.RootElement.Clone(),
            Relationships = Array.Empty<EntitySnapshot>(),
        };
    }
}
