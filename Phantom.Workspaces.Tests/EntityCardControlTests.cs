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
}
