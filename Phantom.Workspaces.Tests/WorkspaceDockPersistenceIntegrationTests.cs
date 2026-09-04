using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Dock.Serializer.SystemTextJson;
using global::Dock.Model.Controls;
using global::Dock.Model.Core;
using MvvmControls = global::Dock.Model.Mvvm.Controls;
using MvvmCore = global::Dock.Model.Mvvm.Core;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;
using Xunit;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// #1335 integration: after restoring a bloated (duplicate-instance) dock-layout, the factory's
/// per-tab registry resolves each tab Id to the single canonical <see cref="WorkspaceDocument"/>
/// instance actually rendered under a <see cref="WorkspaceContentDock"/>, with no stale duplicate
/// instance left reachable in the tree.
/// </summary>
public sealed class WorkspaceDockPersistenceIntegrationTests
{
    [AvaloniaFact(Timeout = 20_000)]
    public async Task WorkspaceDockFactory_GetDocumentForTab_AfterRestoreOfBloatedTree_ReturnsSingleCanonicalInstance()
    {
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        var entityBroker = MainWindowIntegrationTests.GetEntityBroker(viewModel);

        var layoutJson = BuildBloatedSingleRegionLayoutJson("bloat-0", "bloat-1");

        var workspaceId = new EntityId("d0c1a7a0-1335-4000-8000-000000000001");
        var workspaceJson = $$"""
            {
              "entity-id": "{{workspaceId.Value}}",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "1335 Bloat Restore WS" },
              "dock-layout": {{layoutJson}},
              "regions": []
            }
            """;
        await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(entityBroker, workspaceId, workspaceJson);
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var pane = viewModel.WorkspacePanes.Single(
            p => string.Equals(p.Id, workspaceId.ToString(), StringComparison.Ordinal));
        await MainWindowIntegrationTests.WaitForPanePopulatedAsync(pane);

        var root = (IRootDock)pane.ContentLayout!;
        var primaryDock = MultiRegionRestoreTestSupport.EnumerateDocks(root)
            .OfType<WorkspaceContentDock>()
            .First();
        await MultiRegionRestoreTestSupport.WaitForDockableCountAsync(primaryDock, 2);

        var resolved = pane.GetDocumentForTab("bloat-0");
        Assert.NotNull(resolved);

        // The resolved document is the one actually hosted under a WorkspaceContentDock.
        var owner = Assert.IsType<WorkspaceContentDock>(resolved!.Owner);
        Assert.Contains(resolved, owner.VisibleDockables!);

        // #1335 core guarantee: serializing the live layout back out (the save path) is canonical —
        // exactly one document per live tab Id, no orphan floating windows — so a
        // save → restore → save cycle cannot grow the persisted graph. A stale ActiveDockable stub
        // left behind by the primary generator must NOT survive the canonical write.
        // Mirror WriteBackWorkspaceTabs: refresh each live document's descriptor before serializing.
        foreach (var openDoc in DockLayoutCanonicalizer.CollectAllDockables(root).OfType<WorkspaceDocument>())
        {
            if (openDoc.TabViewModel is { } liveTab)
            {
                var refreshed = WorkspaceDocument.BuildDescriptor(liveTab);
                if (refreshed is not null)
                {
                    openDoc.Descriptor = refreshed;
                }
            }
        }

        var liveTabIds = new[] { "bloat-0", "bloat-1" };
        var savedJson = DockLayoutCanonicalizer.SerializeCanonical(root, liveTabIds);
        var reloaded = DockLayoutCanonicalizer.Deserialize(savedJson);
        Assert.NotNull(reloaded);

        var reloadedDocs = DockLayoutCanonicalizer.CollectAllDockables(reloaded!)
            .OfType<WorkspaceDocument>()
            .ToList();
        Assert.Equal(2, reloadedDocs.Count);
        Assert.Single(reloadedDocs, d => d.Id == "bloat-0");
        Assert.Single(reloadedDocs, d => d.Id == "bloat-1");
        Assert.Empty(DockLayoutCanonicalizer.CollectAllWindows(reloaded!));
    }

    /// <summary>
    /// Builds a legacy-format (DockSerializer) single-region layout deliberately bloated the way
    /// the pre-#1335 save path leaked: the primary region's documents are duplicated into a sibling
    /// region and into an orphan floating window, and the root's ActiveDockable/DefaultDockable are
    /// inline clones. On restore, the #1335 heal pass must collapse all of this to one canonical
    /// instance per tab Id.
    /// </summary>
    private static string BuildBloatedSingleRegionLayoutJson(params string[] tabIds)
    {
        WorkspaceDocument Doc(string id) => new(new WebViewModel($"https://example.com/{id}") { Id = id, Title = id });

        WorkspaceContentDock Region(string dockId, params string[] ids)
        {
            var docs = ids.Select(Doc).ToList();
            var dock = new WorkspaceContentDock
            {
                Id = dockId,
                VisibleDockables = new ObservableCollection<IDockable>(docs.Cast<IDockable>()),
                ActiveDockable = docs[0],
            };
            foreach (var d in docs)
            {
                d.Owner = dock;
            }

            return dock;
        }

        var primary = Region("primary-region", tabIds);
        var siblingDuplicate = Region("sibling-region", tabIds); // duplicate instances, same Ids
        var splitter = new MvvmControls.ProportionalDockSplitter { Id = "prop-splitter" };
        var proportional = new MvvmControls.ProportionalDock
        {
            Id = "prop",
            VisibleDockables = new ObservableCollection<IDockable> { primary, splitter, siblingDuplicate },
        };
        primary.Owner = proportional;
        splitter.Owner = proportional;
        siblingDuplicate.Owner = proportional;

        // Orphan floating window holding yet another duplicate of the tabs.
        var windowLayout = new MvvmControls.RootDock
        {
            Id = "float-root",
            VisibleDockables = new ObservableCollection<IDockable> { Region("float-region", tabIds) },
        };
        var window = new MvvmCore.DockWindow { Id = "orphan-window", Layout = windowLayout };

        var root = new MvvmControls.RootDock
        {
            Id = "content-root",
            VisibleDockables = new ObservableCollection<IDockable> { proportional },
            ActiveDockable = proportional,
            DefaultDockable = proportional,
            Windows = new ObservableCollection<IDockWindow> { window },
        };
        proportional.Owner = root;

        var serializer = new DockSerializer(typeof(ObservableCollection<>), new WorkspaceDockTypeInfoResolver());
        return serializer.Serialize<IRootDock>(root);
    }
}
