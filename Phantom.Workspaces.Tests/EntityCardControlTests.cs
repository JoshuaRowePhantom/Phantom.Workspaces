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

    // Issue #1214: every text-data element in the entity card is a SafeSelectableTextBlock so it
    // can be selected/copied, while preserving wrapping and highlight-match runs.
    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityCardControl_DisplayName_RendersAsSafeSelectableTextBlock()
    {
        var card = new EntityCardControl { DataContext = await BuildToolNoteCardViewModelAsync() };
        var window = new Window { Content = card };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var title = window.GetVisualDescendants()
                .OfType<SafeSelectableTextBlock>()
                .FirstOrDefault(t => t.Classes.Contains("workspace-entity-title"));

            Assert.NotNull(title);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityCardControl_EntityTypeLabel_RendersAsSafeSelectableTextBlock()
    {
        var card = new EntityCardControl { DataContext = await BuildToolNoteCardViewModelAsync() };
        var window = new Window { Content = card };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var typeLabel = window.GetVisualDescendants()
                .OfType<SafeSelectableTextBlock>()
                .FirstOrDefault(t => t.Text is { } text
                    && text.Contains("tool", StringComparison.Ordinal)
                    && text.Contains("note", StringComparison.Ordinal));

            Assert.NotNull(typeLabel);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityCardControl_DisplayName_PreservesHighlightMatchRuns()
    {
        var card = new EntityCardControl { DataContext = await BuildToolNoteCardViewModelAsync() };
        var window = new Window { Content = card };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var title = window.GetVisualDescendants()
                .OfType<SafeSelectableTextBlock>()
                .First(t => t.Classes.Contains("workspace-entity-title"));

            Assert.NotNull(title.Inlines);
            var runs = title.Inlines!.OfType<Avalonia.Controls.Documents.Run>().ToArray();
            Assert.True(runs.Length >= 3, $"Expected at least 3 runs, found {runs.Length}.");
            var runText = string.Concat(runs.Select(r => r.Text));
            Assert.Contains("Run VS Code Tunnel", runText, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardControl_FieldReadMode_ValueIsSafeSelectableTextBlock()
    {
        var entity = new SubscribedEntityViewModel(BuildToolNoteSnapshotForTests());
        var fieldEditors = new EntityFieldEditorViewModel[]
        {
            new StringFieldEditorViewModel("path", "/home/user/worktrees/9"),
        };
        var card = new EntityCardControl { DataContext = new EntityCardViewModel(entity, fieldEditors) };
        var window = new Window { Content = card, Width = 400, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var readValue = window.GetVisualDescendants()
                .OfType<SafeSelectableTextBlock>()
                .FirstOrDefault(t => t.Classes.Contains("workspace-field-read-value"));
            Assert.NotNull(readValue);
            Assert.Equal("/home/user/worktrees/9", readValue!.Text);

            // The field label is copyable too, and no read value remains a plain TextBlock.
            var plainReadValues = window.GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(t => t is not SafeSelectableTextBlock && t.Classes.Contains("workspace-field-read-value"))
                .ToArray();
            Assert.Empty(plainReadValues);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityCardControl_FieldLabels_AreSafeSelectableTextBlock()
    {
        var card = new EntityCardControl { DataContext = await BuildToolNoteCardViewModelAsync() };
        var window = new Window { Content = card };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var labels = window.GetVisualDescendants()
                .OfType<SafeSelectableTextBlock>()
                .Where(t => t.Classes.Contains("workspace-field-label"))
                .ToArray();

            Assert.NotEmpty(labels);
            // No field label may remain a plain (non-selectable) TextBlock.
            var plainLabels = window.GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(t => t is not SafeSelectableTextBlock && t.Classes.Contains("workspace-field-label"))
                .ToArray();
            Assert.Empty(plainLabels);
        }
        finally
        {
            window.Close();
        }
    }

    // Issue #1214: for a git-worktree entity card, every rendered property value element
    // (path / branch / head-commit / target-branch) must be a copyable SafeSelectableTextBlock.
    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardControl_GitWorktree_AllPropertyValuesAreCopyable()
    {
        var entity = new SubscribedEntityViewModel(BuildGitWorktreeSnapshotForTests());
        var fieldEditors = new EntityFieldEditorViewModel[]
        {
            new StringFieldEditorViewModel("path", "/home/user/worktrees/9"),
            new StringFieldEditorViewModel("branch", "feature/wrap-fix"),
            new StringFieldEditorViewModel("head-commit", "a1b2c3d4e5f6"),
            new StringFieldEditorViewModel("target-branch", "main"),
        };
        var card = new EntityCardControl { DataContext = new EntityCardViewModel(entity, fieldEditors) };
        var window = new Window { Content = card, Width = 500, Height = 500 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var readValueTexts = window.GetVisualDescendants()
                .OfType<SafeSelectableTextBlock>()
                .Where(t => t.Classes.Contains("workspace-field-read-value"))
                .Select(t => t.Text)
                .ToArray();

            foreach (var expected in new[] { "/home/user/worktrees/9", "feature/wrap-fix", "a1b2c3d4e5f6", "main" })
            {
                Assert.Contains(expected, readValueTexts);
            }

            // No property value may remain a plain (non-copyable) TextBlock.
            var plainReadValues = window.GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(t => t is not SafeSelectableTextBlock && t.Classes.Contains("workspace-field-read-value"))
                .ToArray();
            Assert.Empty(plainReadValues);
        }
        finally
        {
            window.Close();
        }
    }

    // Issue #1214: regression guard for #1006/#1177 — header-row selectable text blocks still wrap
    // (word-level, not character-clipped) when the row is constrained narrow after the swap to
    // SafeSelectableTextBlock.
    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityCardControl_HeaderRow_TextBlocksWrapWhenNarrow()
    {
        var card = new EntityCardControl { DataContext = await BuildToolNoteCardViewModelAsync() };
        var window = new Window { Content = card, Width = 90, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var title = window.GetVisualDescendants()
                .OfType<SafeSelectableTextBlock>()
                .First(t => t.Classes.Contains("workspace-entity-title"));

            Assert.Equal(Avalonia.Media.TextWrapping.Wrap, title.TextWrapping);
            Assert.True(
                title.TextLayout.TextLines.Count >= 2,
                $"Header-row title should wrap to multiple lines when narrow; got {title.TextLayout.TextLines.Count} line(s).");
        }
        finally
        {
            window.Close();
        }
    }

    // Issue #1214: regression guard — the read-mode field value SafeSelectableTextBlock still wraps
    // under the workspace-field-read-value style when the value column is narrow.
    [AvaloniaFact(Timeout = 15_000)]
    public void EntityCardControl_FieldReadValue_WrapsWhenNarrow()
    {
        var entity = new SubscribedEntityViewModel(BuildToolNoteSnapshotForTests());
        var fieldEditors = new EntityFieldEditorViewModel[]
        {
            new StringFieldEditorViewModel(
                "path",
                "the quick brown fox jumps over the lazy dog several times over again here"),
        };
        var card = new EntityCardControl { DataContext = new EntityCardViewModel(entity, fieldEditors) };
        var window = new Window { Content = card, Width = 160, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var readValue = window.GetVisualDescendants()
                .OfType<SafeSelectableTextBlock>()
                .First(t => t.Classes.Contains("workspace-field-read-value"));

            Assert.Equal(Avalonia.Media.TextWrapping.Wrap, readValue.TextWrapping);
            Assert.True(
                readValue.TextLayout.TextLines.Count >= 2,
                $"Read-mode field value should wrap to multiple lines when narrow; got {readValue.TextLayout.TextLines.Count} line(s).");
        }
        finally
        {
            window.Close();
        }
    }

    // Issue #1213: rendering EntityCardControl at a narrow width must not character-clip the display
    // name — the header wrap layout reflows the name across multiple lines at word boundaries and
    // preserves every character.
    [AvaloniaFact(Timeout = 15_000)]
    public async Task EntityCardControl_HeaderMeasuredNarrow_DoesNotClipDisplayName()
    {
        var card = new EntityCardControl { DataContext = await BuildToolNoteCardViewModelAsync() };
        var window = new Window { Content = card, Width = 90, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var title = window.GetVisualDescendants()
                .OfType<SafeSelectableTextBlock>()
                .First(t => t.Classes.Contains("workspace-entity-title"));

            // Word-boundary wrapping (not character clipping): the title uses TextWrapping=Wrap and
            // reflows the display name across multiple lines rather than truncating it.
            Assert.Equal(Avalonia.Media.TextWrapping.Wrap, title.TextWrapping);
            var lines = title.TextLayout.TextLines;
            Assert.True(lines.Count >= 2, $"Expected wrapped display name, got {lines.Count} line(s).");

            // Every character of the display name remains present across the wrapped lines.
            var renderedLength = lines.Sum(l => l.Length);
            var displayName = string.Concat(
                title.Inlines!.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text));
            Assert.True(
                renderedLength >= displayName.Length,
                $"Rendered {renderedLength} chars < display name {displayName.Length}; text was clipped.");
        }
        finally
        {
            window.Close();
        }
    }

    private static EntitySnapshot BuildGitWorktreeSnapshotForTests()
    {
        var entityId = Guid.NewGuid();
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity", "git-worktree"],
              "names": [["git-worktrees", "wt-{{entityId:N}}"]],
              "display-name": { "default": "worktree, system-defined" }
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
