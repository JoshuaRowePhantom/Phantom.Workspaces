using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using global::Dock.Model.Controls;
using global::Dock.Model.Core;
using MvvmControls = global::Dock.Model.Mvvm.Controls;
using MvvmCore = global::Dock.Model.Mvvm.Core;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// #1335: verifies the dock-layout persistence leak fix. The canonical single-serialize write path
/// emits <c>ActiveDockable</c>/<c>DefaultDockable</c>/<c>FocusedDockable</c> as <c>$ref</c> (not
/// inline <c>$id</c> clones), and the heal-on-load pass dedupes duplicate documents by Id, prunes
/// empty non-primary docks / secondary roots, and prunes orphan floating windows.
/// </summary>
public sealed class WorkspaceDockPersistenceTests
{
    private static WorkspaceDocument CreateWebDoc(string id, string url)
        => new(new WebViewModel(url) { Id = id, Title = id });

    private static WorkspaceContentDock CreateContentDock(string id, IEnumerable<WorkspaceDocument> docs)
    {
        var list = docs.ToList();
        var dock = new WorkspaceContentDock
        {
            Id = id,
            VisibleDockables = new ObservableCollection<IDockable>(list),
            ActiveDockable = list.FirstOrDefault(),
        };
        foreach (var d in list)
        {
            d.Owner = dock;
        }

        return dock;
    }

    private static MvvmControls.RootDock WrapInRoot(string id, IDock content)
    {
        var root = new MvvmControls.RootDock
        {
            Id = id,
            VisibleDockables = new ObservableCollection<IDockable> { content },
            ActiveDockable = content,
            DefaultDockable = content,
        };
        content.Owner = root;
        return root;
    }

    private static int CountByExactType(IRootDock root, Type type)
        => DockLayoutCanonicalizer.CollectAllDockables(root).Count(d => d.GetType() == type);

    private static List<WorkspaceDocument> AllDocs(IRootDock root)
        => DockLayoutCanonicalizer.CollectAllDockables(root).OfType<WorkspaceDocument>().ToList();

    // ── Test 1: repeated save/restore/save keeps exactly one document per tab ────────────

    [Fact]
    public void WorkspaceDockPersistence_SaveRestoreSave_DocumentCountEqualsTabCount()
    {
        var ids = Enumerable.Range(0, 11).Select(i => $"tab-{i}").ToArray();
        var docs = ids.Select(id => CreateWebDoc(id, $"https://example.com/{id}"));
        IRootDock root = WrapInRoot("content-root", CreateContentDock("primary", docs));

        // Simulate several save → restore → save cycles. Before the fix, AD/DD/FD inline clones
        // multiplied the document graph on every cycle; after the fix it stays canonical.
        for (var cycle = 0; cycle < 4; cycle++)
        {
            var json = DockLayoutCanonicalizer.SerializeCanonical(root, ids);
            var restored = DockLayoutCanonicalizer.Deserialize(json);
            Assert.NotNull(restored);
            DockLayoutCanonicalizer.Canonicalize(restored!, liveTabIds: null);
            root = restored!;
        }

        var docsById = AllDocs(root).Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(11, AllDocs(root).Count);
        Assert.Equal(11, docsById.Count);
        Assert.Equal(1, CountByExactType(root, typeof(MvvmControls.RootDock)));
        Assert.Equal(0, CountByExactType(root, typeof(MvvmControls.DocumentDock)));
        Assert.Empty(DockLayoutCanonicalizer.CollectAllWindows(root));
    }

    // ── Test 2: shared ActiveDockable serializes as $ref, not a clone ────────────────────

    [Fact]
    public void WorkspaceDockPersistence_ActiveDockableSharesInstanceWithVisibleDockables_EmitsRefNotClone()
    {
        var doc = CreateWebDoc("shared-tab", "https://example.com/shared");
        var dock = CreateContentDock("primary", new[] { doc });
        dock.ActiveDockable = doc; // same instance as the sole VisibleDockables child
        var root = WrapInRoot("content-root", dock);

        var json = DockLayoutCanonicalizer.SerializeCanonical(root, new[] { "shared-tab" });

        // Exactly one inline WorkspaceDocument object exists; AD points back to it with $ref.
        var occurrences = CountOccurrences(json, "\"" + typeof(WorkspaceDocument).FullName + "\"");
        Assert.Equal(1, occurrences);
        Assert.Contains("$ref", json);
        Assert.Contains("\"ActiveDockable\":{\"$ref\":", json);
    }

    // ── Test 3: closing the last floating window prunes the orphan DockWindow ────────────

    [Fact]
    public void WorkspaceDockPersistence_ClosingLastFloatingWindow_PrunesOrphanDockWindow()
    {
        var liveDoc = CreateWebDoc("live-1", "https://example.com/live");
        var primary = CreateContentDock("primary", new[] { liveDoc });
        var root = WrapInRoot("content-root", primary);

        // A floating window whose only document is a tab that has since been closed (its Id is not
        // in the live-tab set). This models the orphan DockWindow left behind after closing the last
        // floating window.
        var staleDoc = CreateWebDoc("closed-1", "https://example.com/closed");
        var windowLayout = WrapInRoot("float-root", CreateContentDock("float-dock", new[] { staleDoc }));
        var window = new MvvmCore.DockWindow { Id = "orphan-window", Layout = windowLayout };
        root.Windows = new ObservableCollection<IDockWindow> { window };

        DockLayoutCanonicalizer.Canonicalize(root, liveTabIds: new[] { "live-1" });

        Assert.Empty(root.Windows!);
        Assert.Empty(DockLayoutCanonicalizer.CollectAllWindows(root));
    }

    // ── Test 4: real bloated fixture heals to the canonical graph ────────────────────────

    [Fact]
    public void WorkspaceDockPersistence_LoadRealLayout1334_HealsTo11Docs()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "dock-layout-1334.json");
        var json = File.ReadAllText(fixturePath);

        var layout = DockLayoutCanonicalizer.Deserialize(json);
        Assert.NotNull(layout);

        DockLayoutCanonicalizer.Canonicalize(layout!, liveTabIds: null);
        MainWindowViewModel.MigrateBaseDocumentDocksToWorkspaceContentDock(layout!);

        Assert.Equal(11, AllDocs(layout!).Count);
        Assert.Equal(1, CountByExactType(layout!, typeof(MvvmControls.RootDock)));
        Assert.Equal(0, CountByExactType(layout!, typeof(MvvmControls.DocumentDock)));
        Assert.Empty(DockLayoutCanonicalizer.CollectAllWindows(layout!));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
