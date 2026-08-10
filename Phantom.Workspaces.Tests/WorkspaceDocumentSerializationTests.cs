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
/// Properties like TabViewModel are excluded via [JsonIgnore]. Owner back-references are
/// handled by ReferenceHandler.Preserve ($ref markers) — no shadow property is needed.
/// Context is excluded via [IgnoreDataMember] on the base DockableBase class.
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

    [Fact]
    public void WorkspaceDocument_Descriptor_BrowserKind_LongTitleRoundTripsLosslessly()
    {
        const string longTitle = "Consolidate duplicated JSON serializer options + default config-path logic (AllowedSecretsStore vs ConfigurationPersistenceService)";
        var descriptor = new BrowserDockTabDescriptor("https://roundtrip.example.com/long-title")
        {
            Title = longTitle,
            IsTitleExplicit = false,
        };
        var tab = new StubWorkspaceTab("tab-rt-browser-long-title", "Browser Round-trip");
        var doc = new WorkspaceDocument(tab) { Descriptor = descriptor };

        var serializer = new DockSerializer(typeof(ObservableCollection<>));
        var json = serializer.Serialize(doc);
        var restored = serializer.Deserialize<WorkspaceDocument>(json);

        Assert.NotNull(restored);
        var restoredDesc = Assert.IsType<BrowserDockTabDescriptor>(restored!.Descriptor);
        Assert.Equal("https://roundtrip.example.com/long-title", restoredDesc.Url);
        Assert.Equal(longTitle, restoredDesc.Title);
        Assert.False(restoredDesc.IsTitleExplicit);
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

    // ── Owner shadow is absent; ReferenceHandler.Preserve handles cycles ────────

    [Fact]
    public void WorkspaceDocument_DoesNotDeclareOwnerShadow()
    {
        // Owner must NOT be declared on WorkspaceDocument — only the base DockableBase
        // defines it. Absence of the shadow lets ReferenceHandler.Preserve detect
        // Owner → ContentDock → Document back-reference cycles correctly.
        var prop = typeof(WorkspaceDocument).GetProperty(
            nameof(WorkspaceDocument.Owner),
            System.Reflection.BindingFlags.DeclaredOnly | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        Assert.Null(prop);
    }

    [Fact]
    public void WorkspacePaneDocument_DoesNotDeclareOwnerShadow()
    {
        var prop = typeof(WorkspacePaneDocument).GetProperty(
            nameof(WorkspacePaneDocument.Owner),
            System.Reflection.BindingFlags.DeclaredOnly | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        Assert.Null(prop);
    }

    [Fact]
    public void WorkspaceDocument_Serialize_WithOwnerSet_DoesNotThrow()
    {
        var tab = new StubWorkspaceTab("tab-cycle", "Cycle Tab");
        var doc = new WorkspaceDocument(tab);

        // Wire the Owner back-reference to simulate post-InitLayout state.
        // ReferenceHandler.Preserve (used by DockSerializer) emits $ref for cycles;
        // no CaptureAndClearOwners is needed.
        var fakeDock = new WorkspaceContentDock { Id = "fake-parent" };
        doc.Owner = fakeDock;

        var serializer = new DockSerializer(typeof(ObservableCollection<>), new WorkspaceDockTypeInfoResolver());
        var ex = Record.Exception(() => serializer.Serialize(doc));
        Assert.Null(ex);
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
            VisibleDockables = new System.Collections.ObjectModel.ObservableCollection<global::Dock.Model.Core.IDockable> { doc1 },
        };
        var dock2 = new WorkspaceContentDock
        {
            Id = "dock-2",
            VisibleDockables = new System.Collections.ObjectModel.ObservableCollection<global::Dock.Model.Core.IDockable> { doc2 },
        };
        var root = new global::Dock.Model.Mvvm.Controls.RootDock
        {
            Id = "root",
            VisibleDockables = new System.Collections.ObjectModel.ObservableCollection<global::Dock.Model.Core.IDockable> { dock1, dock2 },
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
            VisibleDockables = new ObservableCollection<global::Dock.Model.Core.IDockable> { doc },
        };
        contentDock.ActiveDockable = doc;
        contentDock.DefaultDockable = doc;

        var root = new global::Dock.Model.Mvvm.Controls.RootDock
        {
            Id = "root",
            VisibleDockables = new ObservableCollection<global::Dock.Model.Core.IDockable> { contentDock },
        };
        root.ActiveDockable = contentDock;
        root.DefaultDockable = contentDock;

        // Wire Owner back-references (as InitLayout would do at runtime).
        // ReferenceHandler.Preserve handles the cycle via $ref — no workaround needed.
        doc.Owner = contentDock;
        contentDock.Owner = root;

        var serializer = new DockSerializer(typeof(ObservableCollection<>), new WorkspaceDockTypeInfoResolver());
        var ex = Record.Exception(() => serializer.Serialize(root));
        Assert.Null(ex);
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
        var ex = Record.Exception(() => serializer.Deserialize<global::Dock.Model.Controls.IRootDock>(jsonWithExtra));
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
            VisibleDockables = new ObservableCollection<global::Dock.Model.Core.IDockable> { doc },
        };
        contentDock.ActiveDockable = doc;

        var root = new global::Dock.Model.Mvvm.Controls.RootDock
        {
            Id = "root-vm-fields",
            VisibleDockables = new ObservableCollection<global::Dock.Model.Core.IDockable> { contentDock },
        };
        root.ActiveDockable = contentDock;
        // Wire Owner back-references; ReferenceHandler.Preserve handles cycles via $ref.
        doc.Owner = contentDock;
        contentDock.Owner = root;

        var serializer = new DockSerializer(typeof(ObservableCollection<>), new WorkspaceDockTypeInfoResolver());
        var json = serializer.Serialize(root);

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

    // ── #1158: DockTabDescriptor.Title round-trips through DockSerializer ─────

    [Fact]
    public void WorkspaceDocument_Descriptor_EntityKind_PreservesTitle_OnRoundTrip()
    {
        var descriptor = new EntityDockTabDescriptor(
            "11111111-1111-4111-1111-111111111111", "Open")
        {
            Title = "User-Visible Entity Title",
        };
        var tab = new StubWorkspaceTab("tab-title-entity", "User-Visible Entity Title");
        var doc = new WorkspaceDocument(tab) { Descriptor = descriptor };

        var serializer = new DockSerializer(typeof(ObservableCollection<>));
        var json = serializer.Serialize(doc);
        var restored = serializer.Deserialize<WorkspaceDocument>(json);

        Assert.NotNull(restored);
        var restoredDesc = Assert.IsType<EntityDockTabDescriptor>(restored!.Descriptor);
        Assert.Equal("User-Visible Entity Title", restoredDesc.Title);
        Assert.Equal("11111111-1111-4111-1111-111111111111", restoredDesc.EntityId);
        Assert.Equal("Open", restoredDesc.ShortcutName);
    }

    [Fact]
    public void WorkspaceDocument_Descriptor_BrowserKind_PreservesTitle_OnRoundTrip()
    {
        var descriptor = new BrowserDockTabDescriptor("https://title-test.example.com")
        {
            Title = "Custom Browser Tab Title",
        };
        var tab = new StubWorkspaceTab("tab-title-browser", "Custom Browser Tab Title");
        var doc = new WorkspaceDocument(tab) { Descriptor = descriptor };

        var serializer = new DockSerializer(typeof(ObservableCollection<>));
        var json = serializer.Serialize(doc);
        var restored = serializer.Deserialize<WorkspaceDocument>(json);

        Assert.NotNull(restored);
        var restoredDesc = Assert.IsType<BrowserDockTabDescriptor>(restored!.Descriptor);
        Assert.Equal("Custom Browser Tab Title", restoredDesc.Title);
        Assert.Equal("https://title-test.example.com", restoredDesc.Url);
    }

    [Fact]
    public void WorkspaceDocument_Descriptor_AgentSessionKind_PreservesTitle_OnRoundTrip()
    {
        var descriptor = new AgentSessionDockTabDescriptor("22222222-2222-4222-2222-222222222222")
        {
            Title = "Preserved Agent Session Title",
        };
        var tab = new StubWorkspaceTab("tab-title-agent", "Preserved Agent Session Title");
        var doc = new WorkspaceDocument(tab) { Descriptor = descriptor };

        var serializer = new DockSerializer(typeof(ObservableCollection<>));
        var json = serializer.Serialize(doc);
        var restored = serializer.Deserialize<WorkspaceDocument>(json);

        Assert.NotNull(restored);
        var restoredDesc = Assert.IsType<AgentSessionDockTabDescriptor>(restored!.Descriptor);
        Assert.Equal("Preserved Agent Session Title", restoredDesc.Title);
        Assert.Equal("22222222-2222-4222-2222-222222222222", restoredDesc.EntityId);
    }

    [Fact]
    public void WorkspaceDocument_Deserialize_SyncsCachedTabHeaderFromDescriptorTitle()
    {
        const string fullTitle = "GitHub Copilot Workspace Assistant session";
        var doc = new WorkspaceDocument
        {
            Id = "descriptor-title-tab",
            Title = "GitHub Copilot Wo...",
            Descriptor = new AgentSessionDockTabDescriptor("33333333-3333-4333-3333-333333333333")
            {
                Title = fullTitle,
            },
        };

        var serializer = new DockSerializer(typeof(ObservableCollection<>), new WorkspaceDockTypeInfoResolver());
        var restored = serializer.Deserialize<WorkspaceDocument>(serializer.Serialize(doc));

        Assert.NotNull(restored);
        Assert.Equal(fullTitle, restored!.EffectiveTabHeader.Title);
        Assert.Equal(fullTitle, restored.Title);
    }

    [Fact]
    public void WorkspaceDocument_Deserialize_FallsBackToBaseTitleWhenDescriptorMissing()
    {
        var serializer = new DockSerializer(typeof(ObservableCollection<>), new WorkspaceDockTypeInfoResolver());
        const string legacyJson = """
            {
              "Id": "legacy-title-tab",
              "Title": "Legacy Title"
            }
            """;
        var restored = serializer.Deserialize<WorkspaceDocument>(legacyJson);

        Assert.NotNull(restored);
        Assert.Equal("Legacy Title", restored!.EffectiveTabHeader.Title);
    }

    [Fact]
    public void WorkspaceSave_PersistsFullUntruncatedTitleInDescriptorOnly()
    {
        const string longTitle = "GitHub Copilot Workspace Assistant session";
        var tab = new WebViewModel("https://title.example.com")
        {
            Id = "long-browser-title",
            Title = longTitle,
        };
        var doc = new WorkspaceDocument(tab);

        var serializer = new DockSerializer(typeof(ObservableCollection<>), new WorkspaceDockTypeInfoResolver());
        var json = serializer.Serialize(doc);

        using var parsed = JsonDocument.Parse(json);
        Assert.False(parsed.RootElement.TryGetProperty("Title", out _));
        Assert.Equal(longTitle, parsed.RootElement.GetProperty("Descriptor").GetProperty("Title").GetString());
        Assert.DoesNotContain("GitHub Copilot Wo...", json);
    }

    [Fact]
    public void BuildDescriptor_WithNonEmptyTabTitle_CapturesTitleOnDescriptor()
    {
        // Use reflection to call the internal WorkspaceDocument.BuildDescriptor static method
        // against a StubWorkspaceTab that carries a non-empty Title but no Entity — falls into
        // the browser branch when combined with the entity-less path, so use a WebViewModel here
        // to exercise the URL-bearing branch.
        var browserTab = new WebViewModel("https://build-desc.example.com")
        {
            Id = "bd-tab",
            Title = "Non-Empty Title",
        };

        var method = typeof(WorkspaceDocument).GetMethod(
            "BuildDescriptor",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var descriptor = (DockTabDescriptor?)method!.Invoke(null, [browserTab]);
        Assert.NotNull(descriptor);
        var browserDesc = Assert.IsType<BrowserDockTabDescriptor>(descriptor);
        Assert.Equal("Non-Empty Title", browserDesc.Title);
        Assert.Equal("https://build-desc.example.com", browserDesc.Url);
    }

    [Fact]
    public void WorkspaceDocument_Descriptor_IsSerialized_ByDockSerializer()
    {
        // Verifies the complete chain: Descriptor is populated at construction, survives
        // DockSerializer.Serialize, and is reconstructed by DockSerializer.Deserialize.
        var descriptor = new EntityDockTabDescriptor("ffffffff-ffff-4fff-ffff-ffffffffffff", "Open");
        var doc = new WorkspaceDocument(new StubWorkspaceTab("tab-isd", "ISD Tab"))
        {
            Descriptor = descriptor,
        };
        doc.Owner = new WorkspaceContentDock { Id = "parent-isd" };

        var serializer = new DockSerializer(typeof(ObservableCollection<>), new WorkspaceDockTypeInfoResolver());
        var json = serializer.Serialize(doc);

        Assert.Contains("Descriptor", json);
        Assert.Contains("entity", json);
        Assert.Contains("ffffffff-ffff-4fff-ffff-ffffffffffff", json);

        var restored = serializer.Deserialize<WorkspaceDocument>(json);
        Assert.NotNull(restored);
        var restoredDesc = Assert.IsType<EntityDockTabDescriptor>(restored!.Descriptor);
        Assert.Equal("ffffffff-ffff-4fff-ffff-ffffffffffff", restoredDesc.EntityId);
        Assert.Equal("Open", restoredDesc.ShortcutName);
    }

    // ── #1190: Descriptor stays in sync with live tab Title ──────────────────

    [Fact]
    public void WorkspaceDocument_Descriptor_RefreshesTitle_WhenTabTitleChangesAfterInitialize()
    {
        // Regression for #1190: Descriptor was captured once via `??=` at InitializeCore
        // time and never refreshed, so any Title change after Initialize was lost on save.
        var tab = new WebViewModel("https://desc-refresh.example.com")
        {
            Id = "tab-refresh-desc",
            Title = string.Empty,
        };
        var doc = new WorkspaceDocument(tab);

        // At construction time the initial Title is empty, so descriptor.Title is null.
        var initial = Assert.IsType<BrowserDockTabDescriptor>(doc.Descriptor);
        Assert.Null(initial.Title);

        tab.Title = "New Title After Init";

        var refreshed = Assert.IsType<BrowserDockTabDescriptor>(doc.Descriptor);
        Assert.Equal("New Title After Init", refreshed.Title);
        Assert.Equal("https://desc-refresh.example.com", refreshed.Url);
    }

    [Fact]
    public void WorkspaceDocument_Descriptor_SerializedAfterTitleChange_PersistsNewTitle()
    {
        // Same setup as above: after mutating tab.Title, DockSerializer.Serialize(doc)
        // must produce JSON containing the new title, and Deserialize must recover it.
        var tab = new WebViewModel("https://desc-persist.example.com")
        {
            Id = "tab-persist-desc",
            Title = "Original",
        };
        var doc = new WorkspaceDocument(tab);

        tab.Title = "Updated Title";

        var serializer = new DockSerializer(typeof(ObservableCollection<>));
        var json = serializer.Serialize(doc);
        Assert.Contains("Updated Title", json);
        Assert.DoesNotContain("\"Title\":\"Original\"", json);

        var restored = serializer.Deserialize<WorkspaceDocument>(json);
        Assert.NotNull(restored);
        var restoredDesc = Assert.IsType<BrowserDockTabDescriptor>(restored!.Descriptor);
        Assert.Equal("Updated Title", restoredDesc.Title);
    }

    [Theory]
    [InlineData("entity")]
    [InlineData("agent-session")]
    [InlineData("browser")]
    public void DockTabDescriptor_RoundTrip_PreservesTitle_AcrossAllKinds(string kind)
    {
        // Consolidates the per-kind PreservesTitle_OnRoundTrip tests into a parametrised
        // form. Any future descriptor kind must also carry Title through DockSerializer.
        DockTabDescriptor descriptor = kind switch
        {
            "entity" => new EntityDockTabDescriptor(
                "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee", "Open") { Title = "Round-Trip Entity" },
            "agent-session" => new AgentSessionDockTabDescriptor(
                "aaaaaaaa-bbbb-4ccc-8ddd-ffffffffffff") { Title = "Round-Trip Agent" },
            "browser" => new BrowserDockTabDescriptor(
                "https://round-trip.example.com") { Title = "Round-Trip Browser" },
            _ => throw new System.ArgumentOutOfRangeException(nameof(kind)),
        };
        var tab = new StubWorkspaceTab($"tab-rt-{kind}", $"Tab {kind}");
        var doc = new WorkspaceDocument(tab) { Descriptor = descriptor };

        var serializer = new DockSerializer(typeof(ObservableCollection<>));
        var json = serializer.Serialize(doc);
        var restored = serializer.Deserialize<WorkspaceDocument>(json);

        Assert.NotNull(restored);
        Assert.NotNull(restored!.Descriptor);
        Assert.Equal(descriptor.Title, restored.Descriptor!.Title);
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
