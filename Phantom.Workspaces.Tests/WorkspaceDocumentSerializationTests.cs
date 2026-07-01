using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dock.Serializer.SystemTextJson;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// Verifies that DockSerializer.Save never walks into the deep view-model/entity-data graph.
/// Properties like TabViewModel are excluded via [JsonIgnore]. Owner links are excluded via
/// <see cref="MainWindowViewModel.CaptureAndClearOwners"/> (the [JsonIgnore] shadow on new Owner
/// does not override the base [DataMember] Owner in DockModelPolymorphicTypeResolver).
/// StyleKey (System.Type) is stripped by <see cref="WorkspaceDockTypeInfoResolver"/>.
/// </summary>
public sealed class WorkspaceDocumentSerializationTests
{
    // ── [JsonIgnore] attribute checks ────────────────────────────────────────

    [Fact]
    public void WorkspaceDocument_JsonIgnore_TabViewModelNotSerialized()
    {
        var prop = typeof(WorkspaceDocument).GetProperty(nameof(WorkspaceDocument.TabViewModel));
        Assert.NotNull(prop);
        Assert.NotNull(prop.GetCustomAttribute<JsonIgnoreAttribute>());
    }

    [Fact]
    public void WorkspaceDocument_JsonIgnore_EffectiveTabHeaderNotSerialized()
    {
        var prop = typeof(WorkspaceDocument).GetProperty(nameof(WorkspaceDocument.EffectiveTabHeader));
        Assert.NotNull(prop);
        Assert.NotNull(prop.GetCustomAttribute<JsonIgnoreAttribute>());
    }

    [Fact]
    public void WorkspacePaneDocument_JsonIgnore_WorkspacePaneNotSerialized()
    {
        var prop = typeof(WorkspacePaneDocument).GetProperty(nameof(WorkspacePaneDocument.WorkspacePane));
        Assert.NotNull(prop);
        Assert.NotNull(prop.GetCustomAttribute<JsonIgnoreAttribute>());
    }

    // ── DockSerializer.Serialize does not crash ───────────────────────────────

    [Fact]
    public void WriteBackWorkspaceTabs_WithEntityTabOpen_DoesNotThrow()
    {
        var tab = CreateEntityTabWithDeepData("tab-entity-1", "Entity Tab");
        var doc = new WorkspaceDocument(tab);

        var serializer = new DockSerializer(typeof(ObservableCollection<>));
        var ex = Record.Exception(() => serializer.Serialize(doc));
        Assert.Null(ex);
    }

    [Fact]
    public void WriteBackWorkspaceTabs_SerializedJson_ContainsOnlyLayoutProperties()
    {
        var tab = CreateEntityTabWithDeepData("tab-entity-2", "Entity Tab");
        var doc = new WorkspaceDocument(tab);

        var serializer = new DockSerializer(typeof(ObservableCollection<>));
        var json = serializer.Serialize(doc);

        // Layout structural properties must be present
        Assert.Contains("\"Id\"", json);
        Assert.Contains("tab-entity-2", json);

        // Content-bearing properties must be absent
        Assert.DoesNotContain("TabViewModel", json);
        Assert.DoesNotContain("EffectiveTabHeader", json);
        Assert.DoesNotContain("EntitySnapshot", json);
        Assert.DoesNotContain("HasUnreadNotification", json);
    }

    [Fact]
    public void WriteBackWorkspaceTabs_RoundTrips_PaneAndTabStructure()
    {
        var tab = CreateEntityTabWithDeepData("tab-roundtrip", "Round-trip Tab");
        var doc = new WorkspaceDocument(tab);

        var serializer = new DockSerializer(typeof(ObservableCollection<>));
        var json = serializer.Serialize(doc);

        // The JSON must be valid and contain the document Id
        using var parsed = JsonDocument.Parse(json);
        Assert.True(parsed.RootElement.TryGetProperty("Id", out var idProp));
        Assert.Equal("tab-roundtrip", idProp.GetString());
    }

    // ── DockTabDescriptor is embedded in dock-layout JSON ─────────────────────

    [Fact]
    public void WorkspaceDocument_Descriptor_IsNotJsonIgnored()
    {
        var prop = typeof(WorkspaceDocument).GetProperty(nameof(WorkspaceDocument.Descriptor));
        Assert.NotNull(prop);
        Assert.Null(prop!.GetCustomAttribute<JsonIgnoreAttribute>());
    }

    [Fact]
    public void WorkspaceDocument_Descriptor_EntityKind_IsSerializedByDockSerializer()
    {
        var tab = CreateEntityTabWithDeepData("tab-desc-entity", "Entity With Descriptor");
        var doc = new WorkspaceDocument(tab);

        var serializer = new DockSerializer(typeof(ObservableCollection<>));
        var json = serializer.Serialize(doc);

        // Descriptor properties must appear in the serialized layout JSON
        Assert.Contains("Descriptor", json);
        Assert.Contains("entity", json);
    }

    [Fact]
    public void WorkspaceDocument_Descriptor_BrowserKind_IsSerializedWithUrl()
    {
        var tab = new StubWorkspaceTab("tab-desc-browser", "Browser Tab");
        var doc = new WorkspaceDocument(tab)
        {
            Descriptor = new BrowserDockTabDescriptor("https://example.com"),
        };

        var serializer = new DockSerializer(typeof(ObservableCollection<>));
        var json = serializer.Serialize(doc);

        Assert.Contains("Descriptor", json);
        Assert.Contains("browser", json);
        Assert.Contains("https://example.com", json);
    }

    [Fact]
    public void WorkspaceDocument_Descriptor_NullDescriptor_IsDeserializedAsNull()
    {
        var tab = new StubWorkspaceTab("tab-null-desc", "No Descriptor Tab");
        var doc = new WorkspaceDocument(tab);

        Assert.Null(doc.Descriptor);

        var serializer = new DockSerializer(typeof(ObservableCollection<>));
        var json = serializer.Serialize(doc);
        var restored = serializer.Deserialize<WorkspaceDocument>(json);

        Assert.NotNull(restored);
        Assert.Null(restored!.Descriptor);
    }

    [Fact]
    public void WorkspaceDocument_Descriptor_EntityKind_RoundTrips()
    {
        var descriptor = new EntityDockTabDescriptor("aaaaaaaa-aaaa-4aaa-aaaa-aaaaaaaaaaaa", "Open");
        var tab = new StubWorkspaceTab("tab-rt-entity", "Entity Round-trip");
        var doc = new WorkspaceDocument(tab) { Descriptor = descriptor };

        var serializer = new DockSerializer(typeof(ObservableCollection<>));
        var json = serializer.Serialize(doc);
        var restored = serializer.Deserialize<WorkspaceDocument>(json);

        Assert.NotNull(restored);
        var restoredDesc = Assert.IsType<EntityDockTabDescriptor>(restored!.Descriptor);
        Assert.Equal("aaaaaaaa-aaaa-4aaa-aaaa-aaaaaaaaaaaa", restoredDesc.EntityId);
        Assert.Equal("Open", restoredDesc.ShortcutName);
    }

    [Fact]
    public void WorkspaceDocument_Descriptor_BrowserKind_RoundTrips()
    {
        var descriptor = new BrowserDockTabDescriptor("https://roundtrip.example.com");
        var tab = new StubWorkspaceTab("tab-rt-browser", "Browser Round-trip");
        var doc = new WorkspaceDocument(tab) { Descriptor = descriptor };

        var serializer = new DockSerializer(typeof(ObservableCollection<>));
        var json = serializer.Serialize(doc);
        var restored = serializer.Deserialize<WorkspaceDocument>(json);

        Assert.NotNull(restored);
        var restoredDesc = Assert.IsType<BrowserDockTabDescriptor>(restored!.Descriptor);
        Assert.Equal("https://roundtrip.example.com", restoredDesc.Url);
    }

    // ── ContextLocator wires tab view model after deserialization ─────────────

    [Fact]
    public void DockStateRestore_PopulatesTabViewModelFromContextLocator()
    {
        var tab = new StubWorkspaceTab("tab-ctx-1", "Context Tab");
        var doc = new WorkspaceDocument();
        doc.Id = "tab-ctx-1";

        // A deserialization stub has no Context until InitDockable runs
        Assert.Null(doc.Context);

        var factory = new WorkspaceDockFactory(null!);
        factory.ContextLocator = new Dictionary<string, Func<object?>>
        {
            [doc.Id] = () => tab,
        };

        factory.InitDockable(doc, null);

        Assert.Same(tab, doc.Context);
        Assert.Same(tab, doc.TabViewModel);
    }

    // ── [JsonIgnore] shadow properties break Owner → ContentDock cycle ────────

    [Fact]
    public void WorkspaceDocument_JsonIgnore_OwnerNotSerialized()
    {
        var prop = typeof(WorkspaceDocument).GetProperty(nameof(WorkspaceDocument.Owner));
        Assert.NotNull(prop);
        Assert.NotNull(prop!.GetCustomAttribute<JsonIgnoreAttribute>());
    }

    [Fact]
    public void WorkspacePaneDocument_JsonIgnore_OwnerNotSerialized()
    {
        var prop = typeof(WorkspacePaneDocument).GetProperty(nameof(WorkspacePaneDocument.Owner));
        Assert.NotNull(prop);
        Assert.NotNull(prop!.GetCustomAttribute<JsonIgnoreAttribute>());
    }

    [Fact]
    public void WorkspaceDocument_Serialize_WithOwnerSet_DoesNotThrow()
    {
        var tab = new StubWorkspaceTab("tab-cycle", "Cycle Tab");
        var doc = new WorkspaceDocument(tab);

        // Simulate post-InitLayout state where Owner points to a parent dock.
        // The production fix is CaptureAndClearOwners (not [JsonIgnore] shadow), because STJ
        // picks the base [DataMember] Owner property rather than the derived shadow.
        var fakeDock = new WorkspaceContentDock { Id = "fake-parent" };
        doc.Owner = fakeDock;

        Assert.Same(fakeDock, doc.Owner);

        var savedOwners = MainWindowViewModel.CaptureAndClearOwners(doc);
        try
        {
            var serializer = new DockSerializer(typeof(ObservableCollection<>), new WorkspaceDockTypeInfoResolver());
            var json = serializer.Serialize(doc);

            // Owner is null after CaptureAndClearOwners; WhenWritingNull → absent from JSON.
            Assert.DoesNotContain("\"Owner\"", json);
            Assert.DoesNotContain("fake-parent", json);
        }
        finally
        {
            MainWindowViewModel.RestoreOwners(savedOwners);
        }

        // Owner is restored correctly after serialization.
        Assert.Same(fakeDock, doc.Owner);
    }

    // ── AgentSession descriptor round-trip ────────────────────────────────────

    [Fact]
    public void DockTabDescriptor_AgentSessionKind_RoundTrips()
    {
        var descriptor = new AgentSessionDockTabDescriptor("bbbbbbbb-bbbb-4bbb-bbbb-bbbbbbbbbbbb");
        var tab = new StubWorkspaceTab("tab-rt-agent", "Agent Round-trip");
        var doc = new WorkspaceDocument(tab) { Descriptor = descriptor };

        var serializer = new DockSerializer(typeof(ObservableCollection<>));
        var json = serializer.Serialize(doc);
        var restored = serializer.Deserialize<WorkspaceDocument>(json);

        Assert.NotNull(restored);
        var restoredDesc = Assert.IsType<AgentSessionDockTabDescriptor>(restored!.Descriptor);
        Assert.Equal("bbbbbbbb-bbbb-4bbb-bbbb-bbbbbbbbbbbb", restoredDesc.EntityId);
    }

    // ── EnumerateAllDocuments walks the full tree ─────────────────────────────

    [Fact]
    public void EnumerateAllDocuments_FindsDocumentsInSplitLayout()
    {
        var doc1 = new WorkspaceDocument(new StubWorkspaceTab("tab-split-1", "Tab 1"));
        var doc2 = new WorkspaceDocument(new StubWorkspaceTab("tab-split-2", "Tab 2"));
        var dock1 = new WorkspaceContentDock
        {
            Id = "dock-1",
            VisibleDockables = new System.Collections.ObjectModel.ObservableCollection<Dock.Model.Core.IDockable> { doc1 },
        };
        var dock2 = new WorkspaceContentDock
        {
            Id = "dock-2",
            VisibleDockables = new System.Collections.ObjectModel.ObservableCollection<Dock.Model.Core.IDockable> { doc2 },
        };
        var root = new Dock.Model.Mvvm.Controls.RootDock
        {
            Id = "root",
            VisibleDockables = new System.Collections.ObjectModel.ObservableCollection<Dock.Model.Core.IDockable> { dock1, dock2 },
        };

        var found = MainWindowViewModel.EnumerateAllDocuments(root).ToList();

        Assert.Equal(2, found.Count);
        Assert.Contains(doc1, found);
        Assert.Contains(doc2, found);
    }

    // ── Full layout with Owner wired — regression for WorkspaceContentDock.Owner cycle ─

    [Fact]
    public void WriteBackWorkspaceTabs_FullLayout_WithOwnerSet_DoesNotThrow()
    {
        // Construct the same tree structure that CreateWorkspaceContentLayout produces
        // after InitLayout wires Owner back-references.
        var descriptor = new EntityDockTabDescriptor("dddddddd-dddd-4ddd-dddd-dddddddddddd", "Open");
        var doc = new WorkspaceDocument(new StubWorkspaceTab("tab-layout-cycle", "Cycle Layout Tab"))
        {
            Descriptor = descriptor,
        };

        var contentDock = new WorkspaceContentDock
        {
            Id = "content-dock",
            VisibleDockables = new ObservableCollection<Dock.Model.Core.IDockable> { doc },
        };
        contentDock.ActiveDockable = doc;
        contentDock.DefaultDockable = doc;

        var root = new Dock.Model.Mvvm.Controls.RootDock
        {
            Id = "root",
            VisibleDockables = new ObservableCollection<Dock.Model.Core.IDockable> { contentDock },
        };
        root.ActiveDockable = contentDock;
        root.DefaultDockable = contentDock;

        // Wire Owner back-references (as InitLayout would do at runtime).
        doc.Owner = contentDock;
        contentDock.Owner = root;

        // CaptureAndClearOwners is the fix: clear Owner before serialization, restore after.
        var savedOwners = MainWindowViewModel.CaptureAndClearOwners(root);
        try
        {
            var serializer = new DockSerializer(typeof(ObservableCollection<>), new WorkspaceDockTypeInfoResolver());
            var ex = Record.Exception(() => serializer.Serialize(root));
            Assert.Null(ex);
        }
        finally
        {
            MainWindowViewModel.RestoreOwners(savedOwners);
        }

        // Owner is restored correctly after serialization.
        Assert.Same(contentDock, doc.Owner);
        Assert.Same(root, contentDock.Owner);
    }

    // ── DockLayout round-trips through save/load without losing descriptor data ─

    [Fact]
    public void DockLayout_RoundTrip_PreservesDescriptor()
    {
        var descriptor = new EntityDockTabDescriptor("cccccccc-cccc-4ccc-cccc-cccccccccccc", "Open");
        var tab = new StubWorkspaceTab("tab-rt-layout", "Layout Round-trip");
        var doc = new WorkspaceDocument(tab) { Descriptor = descriptor };

        // Serialize and deserialize just the WorkspaceDocument (without needing full layout)
        var serializer = new DockSerializer(typeof(ObservableCollection<>));
        var json = serializer.Serialize(doc);
        var restored = serializer.Deserialize<WorkspaceDocument>(json);

        Assert.NotNull(restored);
        var restoredDesc = Assert.IsType<EntityDockTabDescriptor>(restored!.Descriptor);
        Assert.Equal("cccccccc-cccc-4ccc-cccc-cccccccccccc", restoredDesc.EntityId);
    }

    // ── DockLayout unknown top-level property is ignored ─────────────────────

    [Fact]
    public void DockLayout_UnknownTopLevelProperty_IsIgnored()
    {
        // Dock layouts may have extra properties added in future versions.
        // DockSerializer.Deserialize must not throw when unknown fields are present.
        const string jsonWithExtra = """
            {
              "$type": "Dock.Model.Mvvm.Controls.RootDock",
              "Id": "root-unknown-prop",
              "UnknownFutureProperty": "some-value",
              "AnotherUnknown": 42,
              "VisibleDockables": []
            }
            """;

        var serializer = new DockSerializer(typeof(ObservableCollection<>), new WorkspaceDockTypeInfoResolver());
        var ex = Record.Exception(() => serializer.Deserialize<Dock.Model.Controls.IRootDock>(jsonWithExtra));
        Assert.Null(ex);
    }

    // ── Serialized dock layout has no view-model fields (structural check) ───

    [Fact]
    public void WriteBackWorkspaceTabs_SerializedLayout_HasNoViewModelFields()
    {
        var descriptor = new EntityDockTabDescriptor("eeeeeeee-eeee-4eee-eeee-eeeeeeeeeeee", "Open");
        var doc = new WorkspaceDocument(new StubWorkspaceTab("tab-vm-fields", "VM Fields Tab"))
        {
            Descriptor = descriptor,
        };

        var contentDock = new WorkspaceContentDock
        {
            Id = "content-dock-vm",
            VisibleDockables = new ObservableCollection<Dock.Model.Core.IDockable> { doc },
        };
        contentDock.ActiveDockable = doc;

        var root = new Dock.Model.Mvvm.Controls.RootDock
        {
            Id = "root-vm-fields",
            VisibleDockables = new ObservableCollection<Dock.Model.Core.IDockable> { contentDock },
        };
        root.ActiveDockable = contentDock;
        doc.Owner = contentDock;
        contentDock.Owner = root;

        var savedOwners = MainWindowViewModel.CaptureAndClearOwners(root);
        string json;
        try
        {
            var serializer = new DockSerializer(typeof(ObservableCollection<>), new WorkspaceDockTypeInfoResolver());
            json = serializer.Serialize(root);
        }
        finally
        {
            MainWindowViewModel.RestoreOwners(savedOwners);
        }

        // Required structural fields must be present
        Assert.Contains("\"Id\"", json);
        Assert.Contains("root-vm-fields", json);
        Assert.Contains("Descriptor", json);
        Assert.Contains("eeeeeeee-eeee-4eee-eeee-eeeeeeeeeeee", json);

        // View-model fields must be absent
        Assert.DoesNotContain("TabViewModel", json);
        Assert.DoesNotContain("EffectiveTabHeader", json);
        Assert.DoesNotContain("HasUnreadNotification", json);
        Assert.DoesNotContain("EntitySnapshot", json);
        Assert.DoesNotContain("WorkspacePane", json);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a tab with a SubscribedEntityViewModel whose Data is a 65-level-deep
    /// JsonElement — deep enough to trigger the STJ depth-64 limit before the fix.
    /// </summary>
    private static EntityWorkspaceTabViewModel CreateEntityTabWithDeepData(string id, string title)
    {
        // Build a value that is 65 levels deep: { "a": { "a": { ... } } }
        var deepJson = "{}";
        for (var i = 0; i < 65; i++)
            deepJson = $"{{\"a\":{deepJson}}}";

        // Wrap it in the entity envelope. Parse with MaxDepth=200 so the test helper
        // can build the snapshot; the test then verifies that [JsonIgnore] on TabViewModel
        // prevents DockSerializer from ever reaching this deep payload.
        var fullJson = $$"""
            {
              "entity-id": "22222222-2222-2222-2222-222222222222",
              "entity-types": ["entity"],
              "display-name": { "default": "Test" },
              "deep-data": {{deepJson}}
            }
            """;
        using var document = JsonDocument.Parse(fullJson, new JsonDocumentOptions { MaxDepth = 200 });

        var entity = new SubscribedEntityViewModel(
            new EntitySnapshot
            {
                EntityId = new EntityId("22222222-2222-2222-2222-222222222222"),
                ConcurrencyTag = new ConcurrencyTag("1"),
                ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
                Data = document.RootElement.Clone(),
                Relationships = [],
            },
            deleteEntityAsync: null);

        return new EntityWorkspaceTabViewModel
        {
            Id = id,
            Title = title,
            Entity = entity,
            DockRegion = "full",
        };
    }

    private sealed class StubWorkspaceTab : WorkspaceTabViewModel
    {
        [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
        public StubWorkspaceTab(string id, string title)
        {
            this.Id = id;
            this.Title = title;
            this.DockRegion = "full";
        }
    }
}
