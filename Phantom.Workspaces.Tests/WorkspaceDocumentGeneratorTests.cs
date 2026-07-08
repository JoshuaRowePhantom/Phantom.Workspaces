using System;
using System.Collections;
using System.Text.Json;
using Dock.Model.Core;
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
        var wrongContainer = new Dock.Model.Mvvm.Controls.Document { Id = "wrong" };
        generator.PrepareDocumentContainer(StubDock.Instance, wrongContainer, tab, 0);
    }

    [Fact]
    public void ClearDocumentContainer_WithNonMatchingContainer_DoesNotInvokeOnCleared()
    {
        string? clearedId = null;
        var generator = new WorkspaceDocumentGenerator(onCleared: id => clearedId = id);

        var wrongContainer = new Dock.Model.Mvvm.Controls.Document { Id = "wrong" };
        generator.ClearDocumentContainer(StubDock.Instance, wrongContainer, null);

        Assert.Null(clearedId);
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
