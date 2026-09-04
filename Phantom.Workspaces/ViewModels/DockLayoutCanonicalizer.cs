using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Serializer.SystemTextJson;
using MvvmControls = global::Dock.Model.Mvvm.Controls;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// #1335: canonicalizes a workspace dock-layout tree so that exactly one
/// <see cref="WorkspaceDocument"/> instance survives per tab Id, and serializes it through a
/// single <see cref="JsonSerializer.Serialize"/> call with <see cref="ReferenceHandler.Preserve"/>.
/// <para>
/// The legacy <c>DockSerializer.JsonConverterList</c> write path serialized every
/// <see cref="IList{T}"/> in an isolated <see cref="JsonSerializer.Serialize"/> scope, which reset
/// the <see cref="ReferenceHandler.Preserve"/> reference resolver on every list boundary. As a
/// result <c>ActiveDockable</c>/<c>DefaultDockable</c>/<c>FocusedDockable</c> (which at runtime are
/// the SAME instances as one of their siblings in <c>VisibleDockables</c>) were written as full
/// inline <c>$id</c> clones instead of <c>$ref</c> back-references, so every save duplicated the
/// subtree and the persisted layout grew without bound.
/// </para>
/// <para>
/// Serializing the whole tree in one call (no per-list converter) makes AD/DD/FD emit as
/// <c>$ref</c>. <see cref="Canonicalize"/> additionally dedupes duplicate document instances by Id,
/// prunes empty non-primary docks and secondary roots, and prunes orphan floating windows that own
/// no live tab, so a bloated legacy layout heals to its canonical form on first load.
/// </para>
/// </summary>
internal static class DockLayoutCanonicalizer
{
    /// <summary>
    /// Builds the single-call, reference-preserving <see cref="JsonSerializerOptions"/> used by the
    /// #1335 write/read path. Unlike <c>DockSerializer</c> there is no per-list converter, so the
    /// whole graph shares one <see cref="ReferenceHandler.Preserve"/> reference scope and AD/DD/FD
    /// emit as <c>$ref</c>. Collections are written in the standard STJ <c>$values</c> envelope.
    /// </summary>
    internal static JsonSerializerOptions CreatePreserveOptions()
    {
        return new JsonSerializerOptions
        {
            WriteIndented = false,
            ReferenceHandler = ReferenceHandler.Preserve,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            TypeInfoResolver = new WorkspaceDockTypeInfoResolver(),
        };
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="json"/> was written by the #1335 single-serialize
    /// path (standard STJ collections carry a <c>$values</c> envelope). The legacy
    /// <c>DockSerializer.JsonConverterList</c> format writes collections as plain JSON arrays and
    /// therefore never contains <c>"$values"</c>.
    /// </summary>
    internal static bool IsPreserveFormat(string json)
        => json.Contains("\"$values\"", StringComparison.Ordinal);

    /// <summary>
    /// Deserializes a persisted dock-layout, transparently reading BOTH the legacy
    /// <c>DockSerializer</c> format and the #1335 single-serialize (<c>$values</c>) format so that
    /// existing persisted workspaces keep loading.
    /// </summary>
    internal static IRootDock? Deserialize(string json)
    {
        if (IsPreserveFormat(json))
        {
            var layout = JsonSerializer.Deserialize<IRootDock>(json, CreatePreserveOptions());
            if (layout is not null)
            {
                NormalizeCollections(layout);
            }

            return layout;
        }

        var serializer = new DockSerializer(typeof(ObservableCollection<>), new WorkspaceDockTypeInfoResolver());
        return serializer.Deserialize<IRootDock>(MigrateLegacyReferenceMetadata(json));
    }

    /// <summary>
    /// #1348: migrates a legacy (<c>DockSerializer</c>-format, no <c>$values</c>) layout so that it
    /// deserializes under Dock 12.1.0.2. The legacy write path serialized each <c>IList&lt;T&gt;</c> in
    /// an isolated scope, so <see cref="ReferenceHandler.Preserve"/>'s <c>$id</c> counter reset at
    /// every list boundary and reused ids across lists (e.g. <c>$id="1"</c> occurs 105 times in the
    /// #1334 fixture). Dock 12.1.0.2 (upstream PR danipen/Dock #1107) widened the reference-tracking
    /// scope so it no longer resets per list, so the first duplicate id throws
    /// <c>'$id' metadata property '1' conflicts with an existing identifier</c>.
    /// <para>
    /// This pre-pass is a pure JSON transform that STRIPS all <c>$id</c> metadata and drops every
    /// <c>{"$ref":"N"}</c> reference object. In real legacy layouts the only shared references are the
    /// AD/DD/FD/<c>Window</c> back-references (only 3 <c>$ref</c> tokens in the #1334 fixture), all of
    /// which <see cref="Canonicalize"/> rebuilds/prunes on load, so no meaningful sharing is lost;
    /// Dock reconstructs the tree from the inline <c>$type</c> objects. Keyed off legacy-format
    /// detection (not the specific fixture) so any user's already-persisted 12.0.0.2 layout also loads.
    /// </para>
    /// </summary>
    internal static string MigrateLegacyReferenceMetadata(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return json;
        }

        if (root is null)
        {
            return json;
        }

        var stripped = StripReferenceMetadata(root);
        return stripped?.ToJsonString() ?? json;
    }

    /// <summary>
    /// Recursively removes <c>$id</c> metadata and collapses <c>{"$ref":"N"}</c> reference objects to
    /// <c>null</c>. Returns the transformed node, or <c>null</c> when the node was a bare reference
    /// object (so callers can drop it).
    /// </summary>
    private static JsonNode? StripReferenceMetadata(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                if (obj.ContainsKey("$ref"))
                {
                    // A bare reference object points at an already-materialized instance elsewhere.
                    // Legacy layouts only ever share AD/DD/FD/Window, which Canonicalize rebuilds, so
                    // dropping the reference (property becomes null) is safe.
                    return null;
                }

                obj.Remove("$id");

                foreach (var key in obj.Select(kvp => kvp.Key).ToList())
                {
                    var child = obj[key];
                    var replacement = StripReferenceMetadata(child);
                    if (!ReferenceEquals(replacement, child))
                    {
                        obj[key] = replacement;
                    }
                }

                return obj;
            }

            case JsonArray array:
            {
                for (var i = array.Count - 1; i >= 0; i--)
                {
                    var replacement = StripReferenceMetadata(array[i]);
                    if (replacement is null)
                    {
                        // Never leave a null hole in a dockables collection.
                        array.RemoveAt(i);
                    }
                    else if (!ReferenceEquals(replacement, array[i]))
                    {
                        array[i] = replacement;
                    }
                }

                return array;
            }

            default:
                return node;
        }
    }

    /// <summary>Serializes a layout through the single-call reference-preserving path.</summary>
    internal static string Serialize(IRootDock layout)
        => JsonSerializer.Serialize(layout, CreatePreserveOptions());

    /// <summary>
    /// Produces the canonical persisted JSON for a live layout WITHOUT mutating the live UI tree.
    /// The live tree is first cloned via a round-trip through the reference-preserving serializer
    /// (so AD/DD/FD collapse to the same instances as their <c>VisibleDockables</c> siblings), then
    /// <see cref="Canonicalize"/> dedupes/prunes the detached clone, then the clone is re-serialized.
    /// </summary>
    /// <param name="live">The live content layout to persist.</param>
    /// <param name="liveTabIds">
    /// The set of currently-open tab Ids. Documents whose Id is not in this set are treated as stale
    /// and pruned. Pass <c>null</c> to keep every descriptored document reachable from the primary
    /// <c>VisibleDockables</c> tree.
    /// </param>
    internal static string SerializeCanonical(IRootDock live, IReadOnlyCollection<string>? liveTabIds)
    {
        var cloneJson = Serialize(live);
        var clone = JsonSerializer.Deserialize<IRootDock>(cloneJson, CreatePreserveOptions());
        if (clone is null)
        {
            return cloneJson;
        }

        NormalizeCollections(clone);
        Canonicalize(clone, liveTabIds);
        return Serialize(clone);
    }

    /// <summary>
    /// Dedupes duplicate <see cref="WorkspaceDocument"/> instances by Id (keeping the one reachable
    /// from the primary <c>VisibleDockables</c> tree), rewrites every
    /// <c>ActiveDockable</c>/<c>DefaultDockable</c>/<c>FocusedDockable</c> reference to the canonical
    /// instance (or a surviving child), prunes empty non-primary docks and secondary roots, and
    /// prunes floating windows (<c>Window</c> and <c>Windows[]</c>) that own no live tab. Operates in
    /// place on <paramref name="root"/>.
    /// </summary>
    internal static void Canonicalize(IRootDock root, IReadOnlyCollection<string>? liveTabIds)
    {
        var live = liveTabIds is null ? null : new HashSet<string>(liveTabIds, StringComparer.Ordinal);
        var canonical = new Dictionary<string, WorkspaceDocument>(StringComparer.Ordinal);

        bool Eligible(WorkspaceDocument doc)
            => doc.Descriptor is not null
               && !string.IsNullOrEmpty(doc.Id)
               && (live is null || live.Contains(doc.Id));

        // Register canonical instances from the primary VisibleDockables spine only. Documents that
        // exist solely inside AD/DD/FD clone subtrees or inside floating-window layouts are legacy
        // save-path bloat (or stale tabs) and are never canonical: they are detached by
        // RewriteActiveReferences and their windows are removed by PruneWindows.
        void Register(IDockable? dockable)
        {
            foreach (var doc in EnumerateVisibleDocuments(dockable))
            {
                if (Eligible(doc) && !canonical.ContainsKey(doc.Id))
                {
                    canonical[doc.Id] = doc;
                }
            }
        }

        Register(root);

        PruneDock(root, canonical, isRoot: true);
        RewriteActiveReferences(root, canonical);
        PruneWindows(root, canonical);
    }

    /// <summary>
    /// Recursively removes non-canonical <see cref="WorkspaceDocument"/> instances and empty
    /// non-primary docks from a dock's <c>VisibleDockables</c>, then normalizes splitter placement.
    /// </summary>
    private static void PruneDock(IDock dock, Dictionary<string, WorkspaceDocument> canonical, bool isRoot)
    {
        if (dock.VisibleDockables is not { } children)
        {
            return;
        }

        foreach (var child in children.OfType<IDock>().ToList())
        {
            PruneDock(child, canonical, isRoot: false);
        }

        for (var i = children.Count - 1; i >= 0; i--)
        {
            switch (children[i])
            {
                case WorkspaceDocument doc:
                    if (!(doc.Descriptor is not null
                          && !string.IsNullOrEmpty(doc.Id)
                          && canonical.TryGetValue(doc.Id, out var canon)
                          && ReferenceEquals(canon, doc)))
                    {
                        children.RemoveAt(i);
                    }

                    break;

                case IDock childDock when !SubtreeOwnsCanonical(childDock, canonical):
                    // A non-primary region (empty content dock, empty base DocumentDock, empty
                    // ProportionalDock, or a secondary RootDock) that no longer holds any canonical
                    // document. The outermost root is never pruned here.
                    children.RemoveAt(i);
                    break;
            }
        }

        NormalizeSplitters(dock);
    }

    /// <summary>
    /// Rewrites <c>ActiveDockable</c>/<c>DefaultDockable</c>/<c>FocusedDockable</c> across the
    /// surviving <c>VisibleDockables</c> tree so that each points at a canonical document (mapped by
    /// Id) or a surviving child, never at a pruned clone subtree.
    /// </summary>
    private static void RewriteActiveReferences(IDock dock, Dictionary<string, WorkspaceDocument> canonical)
    {
        if (dock.VisibleDockables is null)
        {
            return;
        }

        var reachable = new HashSet<IDockable>(EnumerateVisibleDescendants(dock), ReferenceEqualityComparer.Instance);

        dock.ActiveDockable = ResolveReference(dock, dock.ActiveDockable, canonical, reachable, preferChild: true);
        dock.DefaultDockable = ResolveReference(dock, dock.DefaultDockable, canonical, reachable, preferChild: true);
        dock.FocusedDockable = ResolveReference(dock, dock.FocusedDockable, canonical, reachable, preferChild: false);

        foreach (var child in dock.VisibleDockables.OfType<IDock>())
        {
            RewriteActiveReferences(child, canonical);
        }
    }

    private static IDockable? ResolveReference(
        IDock dock,
        IDockable? value,
        Dictionary<string, WorkspaceDocument> canonical,
        HashSet<IDockable> reachable,
        bool preferChild)
    {
        if (value is null)
        {
            return null;
        }

        if (value is WorkspaceDocument doc
            && !string.IsNullOrEmpty(doc.Id)
            && canonical.TryGetValue(doc.Id, out var canon))
        {
            if (reachable.Contains(canon))
            {
                return canon;
            }

            value = canon;
        }

        if (reachable.Contains(value))
        {
            return value;
        }

        if (!preferChild)
        {
            return null;
        }

        return dock.VisibleDockables?.FirstOrDefault(d => d is not MvvmControls.ProportionalDockSplitter)
            ?? dock.VisibleDockables?.FirstOrDefault();
    }

    /// <summary>
    /// Prunes the root's <c>Window</c> and <c>Windows[]</c> floating windows whose layout owns no
    /// canonical (live-tab) document. A window that owns a tab Id unique to it is kept.
    /// </summary>
    private static void PruneWindows(IRootDock root, Dictionary<string, WorkspaceDocument> canonical)
    {
        if (root.Window is { } window && !WindowOwnsCanonical(window, canonical))
        {
            root.Window = null;
        }

        if (root.Windows is { } windows)
        {
            for (var i = windows.Count - 1; i >= 0; i--)
            {
                if (!WindowOwnsCanonical(windows[i], canonical))
                {
                    windows.RemoveAt(i);
                }
            }
        }
    }

    private static bool WindowOwnsCanonical(IDockWindow window, Dictionary<string, WorkspaceDocument> canonical)
    {
        if (window.Layout is not { } layout)
        {
            return false;
        }

        return EnumerateVisibleDocuments(layout).Any(doc =>
            doc.Descriptor is not null
            && !string.IsNullOrEmpty(doc.Id)
            && canonical.TryGetValue(doc.Id, out var canon)
            && ReferenceEquals(canon, doc));
    }

    private static bool SubtreeOwnsCanonical(IDockable dockable, Dictionary<string, WorkspaceDocument> canonical)
    {
        return EnumerateVisibleDocuments(dockable).Any(doc =>
            doc.Descriptor is not null
            && !string.IsNullOrEmpty(doc.Id)
            && canonical.TryGetValue(doc.Id, out var canon)
            && ReferenceEquals(canon, doc));
    }

    /// <summary>
    /// Removes leading/trailing and consecutive <c>ProportionalDockSplitter</c>
    /// entries left behind after pruning, so a proportional dock always alternates dock/splitter.
    /// </summary>
    private static void NormalizeSplitters(IDock dock)
    {
        if (dock.VisibleDockables is not { } children)
        {
            return;
        }

        var nonSplitters = children
            .Where(c => c is not MvvmControls.ProportionalDockSplitter)
            .ToList();
        var splitters = children
            .OfType<MvvmControls.ProportionalDockSplitter>()
            .ToList();

        var hadSplitter = splitters.Count > 0;
        var rebuilt = new List<IDockable>();
        for (var i = 0; i < nonSplitters.Count; i++)
        {
            if (i > 0 && splitters.Count > 0)
            {
                var splitter = splitters[0];
                splitters.RemoveAt(0);
                rebuilt.Add(splitter);
            }

            rebuilt.Add(nonSplitters[i]);
        }

        if (!hadSplitter || SequenceReferenceEquals(children, rebuilt))
        {
            return;
        }

        children.Clear();
        foreach (var item in rebuilt)
        {
            children.Add(item);
        }
    }

    private static bool SequenceReferenceEquals(IList<IDockable> a, IList<IDockable> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!ReferenceEquals(a[i], b[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Replaces every non-<see cref="ObservableCollection{T}"/> dockable list in the tree with an
    /// <see cref="ObservableCollection{T}"/> (reusing the same child instances) so restored regions
    /// raise <c>CollectionChanged</c> exactly like factory-created ones. Reachable via
    /// <c>VisibleDockables</c>, AD/DD/FD, and floating-window layouts.
    /// </summary>
    internal static void NormalizeCollections(IDockable root)
    {
        var visited = new HashSet<IDockable>(ReferenceEqualityComparer.Instance);
        NormalizeCollectionsCore(root, visited);
    }

    private static void NormalizeCollectionsCore(IDockable? dockable, HashSet<IDockable> visited)
    {
        if (dockable is null || !visited.Add(dockable))
        {
            return;
        }

        if (dockable is IDock dock)
        {
            dock.VisibleDockables = AsObservable(dock.VisibleDockables);
        }

        if (dockable is IRootDock root)
        {
            root.LeftPinnedDockables = AsObservable(root.LeftPinnedDockables);
            root.RightPinnedDockables = AsObservable(root.RightPinnedDockables);
            root.TopPinnedDockables = AsObservable(root.TopPinnedDockables);
            root.BottomPinnedDockables = AsObservable(root.BottomPinnedDockables);
            root.HiddenDockables = AsObservable(root.HiddenDockables);

            NormalizeCollectionsCore(root.Window?.Layout, visited);
            if (root.Windows is not null)
            {
                foreach (var window in root.Windows)
                {
                    NormalizeCollectionsCore(window.Layout, visited);
                }
            }
        }

        if (dockable is IDock nested && nested.VisibleDockables is not null)
        {
            foreach (var child in nested.VisibleDockables)
            {
                NormalizeCollectionsCore(child, visited);
            }

            NormalizeCollectionsCore(nested.ActiveDockable, visited);
            NormalizeCollectionsCore(nested.DefaultDockable, visited);
            NormalizeCollectionsCore(nested.FocusedDockable, visited);
        }
    }

    private static IList<IDockable>? AsObservable(IList<IDockable>? list)
    {
        if (list is null || list is ObservableCollection<IDockable>)
        {
            return list;
        }

        return new ObservableCollection<IDockable>(list);
    }

    /// <summary>
    /// Yields every <see cref="WorkspaceDocument"/> reachable from <paramref name="dockable"/> via
    /// <c>VisibleDockables</c> only (the runtime-rendered tree), each instance once.
    /// </summary>
    private static IEnumerable<WorkspaceDocument> EnumerateVisibleDocuments(IDockable? dockable)
    {
        if (dockable is null)
        {
            yield break;
        }

        if (dockable is WorkspaceDocument doc)
        {
            yield return doc;
        }

        if (dockable is IDock dock && dock.VisibleDockables is not null)
        {
            foreach (var child in dock.VisibleDockables)
            {
                foreach (var found in EnumerateVisibleDocuments(child))
                {
                    yield return found;
                }
            }
        }
    }

    private static IEnumerable<IDockable> EnumerateVisibleDescendants(IDock dock)
    {
        if (dock.VisibleDockables is null)
        {
            yield break;
        }

        foreach (var child in dock.VisibleDockables)
        {
            yield return child;
            if (child is IDock childDock)
            {
                foreach (var descendant in EnumerateVisibleDescendants(childDock))
                {
                    yield return descendant;
                }
            }
        }
    }

    /// <summary>
    /// Yields every distinct <see cref="IDockable"/> reachable from <paramref name="root"/> via
    /// <c>VisibleDockables</c>, <c>ActiveDockable</c>/<c>DefaultDockable</c>/<c>FocusedDockable</c>,
    /// and floating-window layouts (<c>Window</c> and <c>Windows[]</c>). Used by tests to assert the
    /// full object graph after healing.
    /// </summary>
    internal static IReadOnlyList<IDockable> CollectAllDockables(IRootDock root)
    {
        var visited = new HashSet<IDockable>(ReferenceEqualityComparer.Instance);
        var result = new List<IDockable>();
        CollectDockables(root, visited, result);
        return result;
    }

    private static void CollectDockables(IDockable? dockable, HashSet<IDockable> visited, List<IDockable> result)
    {
        if (dockable is null || !visited.Add(dockable))
        {
            return;
        }

        result.Add(dockable);

        if (dockable is IDock dock)
        {
            if (dock.VisibleDockables is not null)
            {
                foreach (var child in dock.VisibleDockables)
                {
                    CollectDockables(child, visited, result);
                }
            }

            CollectDockables(dock.ActiveDockable, visited, result);
            CollectDockables(dock.DefaultDockable, visited, result);
            CollectDockables(dock.FocusedDockable, visited, result);
        }

        if (dockable is IRootDock root)
        {
            CollectDockables(root.Window?.Layout, visited, result);
            if (root.Windows is not null)
            {
                foreach (var window in root.Windows)
                {
                    CollectDockables(window.Layout, visited, result);
                }
            }
        }
    }

    /// <summary>Collects every distinct floating <see cref="IDockWindow"/> reachable from the tree.</summary>
    internal static IReadOnlyList<IDockWindow> CollectAllWindows(IRootDock root)
    {
        var visitedDockables = new HashSet<IDockable>(ReferenceEqualityComparer.Instance);
        var visitedWindows = new HashSet<IDockWindow>(ReferenceEqualityComparer.Instance);
        var result = new List<IDockWindow>();
        CollectWindows(root, visitedDockables, visitedWindows, result);
        return result;
    }

    private static void CollectWindows(
        IDockable? dockable,
        HashSet<IDockable> visitedDockables,
        HashSet<IDockWindow> visitedWindows,
        List<IDockWindow> result)
    {
        if (dockable is null || !visitedDockables.Add(dockable))
        {
            return;
        }

        if (dockable is IRootDock root)
        {
            if (root.Window is { } window && visitedWindows.Add(window))
            {
                result.Add(window);
                CollectWindows(window.Layout, visitedDockables, visitedWindows, result);
            }

            if (root.Windows is not null)
            {
                foreach (var w in root.Windows)
                {
                    if (visitedWindows.Add(w))
                    {
                        result.Add(w);
                        CollectWindows(w.Layout, visitedDockables, visitedWindows, result);
                    }
                }
            }
        }

        if (dockable is IDock dock)
        {
            if (dock.VisibleDockables is not null)
            {
                foreach (var child in dock.VisibleDockables)
                {
                    CollectWindows(child, visitedDockables, visitedWindows, result);
                }
            }

            CollectWindows(dock.ActiveDockable, visitedDockables, visitedWindows, result);
            CollectWindows(dock.DefaultDockable, visitedDockables, visitedWindows, result);
            CollectWindows(dock.FocusedDockable, visitedDockables, visitedWindows, result);
        }
    }
}
