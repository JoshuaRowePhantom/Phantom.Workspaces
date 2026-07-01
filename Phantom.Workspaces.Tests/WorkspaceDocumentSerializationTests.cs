using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dock.Serializer.SystemTextJson;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// Verifies that [JsonIgnore] is applied to content-bearing properties on
/// WorkspaceDocument and WorkspacePaneDocument so that DockSerializer.Save
/// never walks into the deep view-model/entity-data graph.
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
        var doc = new WorkspaceDocument(tab);

        // Context is not set until InitDockable runs
        Assert.Null(doc.Context);

        var factory = new WorkspaceDockFactory(null!);
        factory.ContextLocator = new Dictionary<string, Func<object?>>
        {
            [doc.Id] = () => tab,
        };

        factory.InitDockable(doc, null);

        Assert.Same(tab, doc.Context);
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
