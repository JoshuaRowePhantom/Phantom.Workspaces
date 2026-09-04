using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json;
using global::Dock.Model.Core;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class WorkspaceDocumentGeneratorTests
{
    // ── WorkspaceDocumentGenerator ───────────────────────────────────────────

    [Fact]
    public void CreateDocumentContainer_WithWorkspaceTabViewModel_ReturnsWorkspaceDocument()
    {
        var generator = new WorkspaceDocumentGenerator();
        var tab = new EntityWorkspaceTabViewModel { Id = "tab-1", Title = "Tab One" };

        var result = generator.CreateDocumentContainer(StubDock.Instance, tab, 0);

        // CreateDocumentContainer returns a parameterless stub; TabViewModel is wired
        // by the subsequent PrepareDocumentContainer call.
        Assert.IsType<WorkspaceDocument>(result);
    }

    [Fact]
    public void PrepareDocumentContainer_WiresTabViewModel()
    {
        var generator = new WorkspaceDocumentGenerator();
        var tab = new EntityWorkspaceTabViewModel { Id = "tab-wire", Title = "Wire Tab" };
        var doc = new WorkspaceDocument();

        generator.PrepareDocumentContainer(StubDock.Instance, doc, tab, 0);

        Assert.Same(tab, doc.TabViewModel);
        Assert.Equal("tab-wire", doc.Id);
    }

    [Fact]
    public void CreateDocumentContainer_WithNullItem_ReturnsNull()
    {
        var generator = new WorkspaceDocumentGenerator();

        var result = generator.CreateDocumentContainer(StubDock.Instance, null!, 0);

        Assert.Null(result);
    }

    [Fact]
    public void CreateDocumentContainer_WithWrongType_ReturnsNull()
    {
        var generator = new WorkspaceDocumentGenerator();

        var result = generator.CreateDocumentContainer(StubDock.Instance, "not-a-tab", 0);

        Assert.Null(result);
    }

    [Fact]
    public void PrepareDocumentContainer_SetsIdTitleCanClose()
    {
        var generator = new WorkspaceDocumentGenerator();
        var tab = new EntityWorkspaceTabViewModel { Id = "prep-tab", Title = "Prepared Tab" };
        var doc = new WorkspaceDocument(tab);

        generator.PrepareDocumentContainer(StubDock.Instance, doc, tab, 0);

        Assert.Equal("prep-tab", doc.Id);
        Assert.Equal("Prepared Tab", doc.Title);
        Assert.True(doc.CanClose);
        Assert.Same(tab, doc.Context);
    }

    [Fact]
    public void ClearDocumentContainer_InvokesOnCleared()
    {
        string? clearedId = null;
        var generator = new WorkspaceDocumentGenerator(onCleared: id => clearedId = id);
        var tab = new EntityWorkspaceTabViewModel { Id = "clear-tab", Title = "Clear Tab" };
        var doc = new WorkspaceDocument(tab);
        doc.Id = "clear-tab";

        generator.ClearDocumentContainer(StubDock.Instance, doc, tab);

        Assert.Equal("clear-tab", clearedId);
    }

    [Fact]
    public void PrepareDocumentContainer_InvokesOnPrepared()
    {
        WorkspaceDocument? prepared = null;
        var generator = new WorkspaceDocumentGenerator(onPrepared: doc => prepared = doc);
        var tab = new EntityWorkspaceTabViewModel { Id = "prep-cb-tab", Title = "Prepared Callback Tab" };
        var doc = new WorkspaceDocument(tab);

        generator.PrepareDocumentContainer(StubDock.Instance, doc, tab, 0);

        Assert.Same(doc, prepared);
    }

    [Fact]
    public void PrepareDocumentContainer_WithNonMatchingContainer_DoesNotThrow()
    {
        var generator = new WorkspaceDocumentGenerator();
        var tab = new EntityWorkspaceTabViewModel { Id = "tab-x", Title = "Tab X" };

        // Passing wrong container type should not throw
        var wrongContainer = new global::Dock.Model.Mvvm.Controls.Document { Id = "wrong" };
        generator.PrepareDocumentContainer(StubDock.Instance, wrongContainer, tab, 0);
    }

    [Fact]
    public void ClearDocumentContainer_WithNonMatchingContainer_DoesNotInvokeOnCleared()
    {
        string? clearedId = null;
        var generator = new WorkspaceDocumentGenerator(onCleared: id => clearedId = id);

        var wrongContainer = new global::Dock.Model.Mvvm.Controls.Document { Id = "wrong" };
        generator.ClearDocumentContainer(StubDock.Instance, wrongContainer, null);

        Assert.Null(clearedId);
    }

    // ── #1340: collision-guard orphan healing ────────────────────────────────

    [Fact]
    public void WorkspaceDocumentGenerator_CreateDocumentContainer_StaleOrphanEntry_ReturnsFreshDocument()
    {
        // A registry entry left over from a prior owner whose dock no longer hosts it (the #1340
        // stale-entry mechanism). The orphan's owner dock does not list it in VisibleDockables, so
        // the collision guard must heal by materializing a fresh document instead of returning null.
        var orphanOwner = new global::Dock.Model.Mvvm.Controls.DocumentDock { Id = "prior-owner" };
        var staleDoc = new WorkspaceDocument { Id = "orphan-tab", Owner = orphanOwner };

        var generator = new WorkspaceDocumentGenerator(
            getDocumentForTab: id => string.Equals(id, "orphan-tab", StringComparison.Ordinal) ? staleDoc : null);
        var tab = new EntityWorkspaceTabViewModel { Id = "orphan-tab", Title = "Orphan Tab" };

        var result = generator.CreateDocumentContainer(StubDock.Instance, tab, 0);

        var fresh = Assert.IsType<WorkspaceDocument>(result);
        Assert.NotSame(staleDoc, fresh);
    }

    [Fact]
    public void WorkspaceDocumentGenerator_CreateDocumentContainer_LiveNonPrimaryEntry_ReturnsNull()
    {
        // A live document genuinely hosted in a different (non-primary split) region of the SAME
        // pane: the collision guard must still defer (return null) so the ItemsSource-bound primary
        // dock does not fabricate a duplicate wrapper.
        var liveOwner = new global::Dock.Model.Mvvm.Controls.DocumentDock { Id = "live-owner" };
        var liveDoc = new WorkspaceDocument { Id = "live-tab", Owner = liveOwner };
        liveOwner.VisibleDockables = new List<IDockable> { liveDoc };

        var generator = new WorkspaceDocumentGenerator(
            getDocumentForTab: id => string.Equals(id, "live-tab", StringComparison.Ordinal) ? liveDoc : null);
        var tab = new EntityWorkspaceTabViewModel { Id = "live-tab", Title = "Live Tab" };

        var result = generator.CreateDocumentContainer(StubDock.Instance, tab, 0);

        Assert.Null(result);
    }

    // ── WorkspacePaneDocumentGenerator ──────────────────────────────────────

    [Fact]
    public void PaneGenerator_CreateDocumentContainer_WithWorkspacePaneViewModel_ReturnsWorkspacePaneDocument()
    {
        var generator = new WorkspacePaneDocumentGenerator();
        var pane = CreateTestWorkspacePaneViewModel("pane-gen-1", "Pane One");

        var result = generator.CreateDocumentContainer(StubDock.Instance, pane, 0);

        var doc = Assert.IsType<WorkspacePaneDocument>(result);
        Assert.Same(pane, doc.WorkspacePane);
        Assert.Equal("pane-gen-1", doc.Id);
        Assert.Equal("Pane One", doc.Title);
        Assert.True(doc.CanClose);
    }

    [Fact]
    public void PaneGenerator_CreateDocumentContainer_WithNullItem_ReturnsNull()
    {
        var generator = new WorkspacePaneDocumentGenerator();

        var result = generator.CreateDocumentContainer(StubDock.Instance, null!, 0);

        Assert.Null(result);
    }

    [Fact]
    public void PaneGenerator_CreateDocumentContainer_WithWrongType_ReturnsNull()
    {
        var generator = new WorkspacePaneDocumentGenerator();

        var result = generator.CreateDocumentContainer(StubDock.Instance, "wrong", 0);

        Assert.Null(result);
    }

    [Fact]
    public void PaneGenerator_PrepareDocumentContainer_SetsIdTitleCanClose()
    {
        var generator = new WorkspacePaneDocumentGenerator();
        var pane = CreateTestWorkspacePaneViewModel("prep-pane", "Prepped Pane");
        var doc = new WorkspacePaneDocument(pane);

        generator.PrepareDocumentContainer(StubDock.Instance, doc, pane, 0);

        Assert.Equal("prep-pane", doc.Id);
        Assert.Equal("Prepped Pane", doc.Title);
        Assert.True(doc.CanClose);
        Assert.Same(pane, doc.Context);
    }

    [Fact]
    public void PaneGenerator_ClearDocumentContainer_InvokesOnCleared()
    {
        string? clearedId = null;
        var generator = new WorkspacePaneDocumentGenerator(onCleared: id => clearedId = id);
        var pane = CreateTestWorkspacePaneViewModel("clear-pane", "Clear Pane");
        var doc = new WorkspacePaneDocument(pane);
        doc.Id = "clear-pane";

        generator.ClearDocumentContainer(StubDock.Instance, doc, pane);

        Assert.Equal("clear-pane", clearedId);
    }

    [Fact]
    public void PaneGenerator_PrepareDocumentContainer_InvokesOnPrepared()
    {
        WorkspacePaneDocument? prepared = null;
        var generator = new WorkspacePaneDocumentGenerator(onPrepared: doc => prepared = doc);
        var pane = CreateTestWorkspacePaneViewModel("prep-cb-pane", "Prepared CB Pane");
        var doc = new WorkspacePaneDocument(pane);

        generator.PrepareDocumentContainer(StubDock.Instance, doc, pane, 0);

        Assert.Same(doc, prepared);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static WorkspacePaneViewModel CreateTestWorkspacePaneViewModel(string paneId, string displayName)
    {
        using var jsonDoc = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "aaaaaaaa-0000-4000-8000-aaaaaaaaaaaa",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "{{displayName}}" }
            }
            """);
        var entity = new SubscribedEntityViewModel(
            new EntitySnapshot
            {
                EntityId = new EntityId("aaaaaaaa-0000-4000-8000-aaaaaaaaaaaa"),
                ConcurrencyTag = new ConcurrencyTag("1"),
                ModifiedTime = new Timestamp(System.DateTimeOffset.UtcNow, "1"),
                Data = jsonDoc.RootElement.Clone(),
                Relationships = System.Array.Empty<EntitySnapshot>(),
            });
        return new WorkspacePaneViewModel(entity, paneId, null);
    }

    /// <summary>
    /// Minimal <see cref="IItemsSourceDock"/> stub used when the generator under test
    /// does not access the dock object.
    /// </summary>
    private sealed class StubDock : IItemsSourceDock
    {
        public static readonly StubDock Instance = new();

        public IEnumerable? ItemsSource => null;
        public IDockItemContainerGenerator? ItemContainerGenerator => null;
        public object? DocumentItemContainerTheme { get; set; }
        public IDocumentItemTemplateSelector? DocumentItemTemplateSelector { get; set; }
        public bool? CanUpdateItemsSourceOnUnregister { get; set; }
        public bool IsDocumentFromItemsSource(IDockable document) => false;
        public bool RemoveItemFromSource(object? item) => false;
    }
}
