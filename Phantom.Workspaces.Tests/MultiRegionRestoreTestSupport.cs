using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Reflection;
using global::Dock.Model.Core;
using MvvmControls = global::Dock.Model.Mvvm.Controls;
using Dock.Serializer.SystemTextJson;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// Shared helpers for #1333 multi-region dock-layout restore tests. Builds a persisted
/// dock-layout JSON with a <see cref="MvvmControls.ProportionalDock"/> containing two
/// <see cref="WorkspaceContentDock"/> regions (left/right), each hosting one web tab.
/// </summary>
internal static class MultiRegionRestoreTestSupport
{
    /// <summary>
    /// Serializes a two-region layout: RootDock → ProportionalDock [ leftdock(leftTab),
    /// splitter, rightdock(rightTab) ]. Region dock ids are <paramref name="leftDockId"/>
    /// and <paramref name="rightDockId"/>; tab ids are <paramref name="leftTabId"/> /
    /// <paramref name="rightTabId"/>.
    /// </summary>
    public static string BuildTwoRegionDockLayoutJson(
        string leftDockId,
        string leftTabId,
        string leftUrl,
        string rightDockId,
        string rightTabId,
        string rightUrl)
    {
        var leftDoc = new WorkspaceDocument(new WebViewModel(leftUrl) { Id = leftTabId, Title = "Left" });
        var rightDoc = new WorkspaceDocument(new WebViewModel(rightUrl) { Id = rightTabId, Title = "Right" });

        var leftDock = new WorkspaceContentDock
        {
            Id = leftDockId,
            VisibleDockables = new ObservableCollection<IDockable> { leftDoc },
            ActiveDockable = leftDoc,
        };
        leftDoc.Owner = leftDock;

        var rightDock = new WorkspaceContentDock
        {
            Id = rightDockId,
            VisibleDockables = new ObservableCollection<IDockable> { rightDoc },
            ActiveDockable = rightDoc,
        };
        rightDoc.Owner = rightDock;

        var splitter = new MvvmControls.ProportionalDockSplitter { Id = $"{leftDockId}-splitter" };
        var proportional = new MvvmControls.ProportionalDock
        {
            Id = $"{leftDockId}-prop",
            VisibleDockables = new ObservableCollection<IDockable> { leftDock, splitter, rightDock },
        };
        leftDock.Owner = proportional;
        splitter.Owner = proportional;
        rightDock.Owner = proportional;

        var root = new MvvmControls.RootDock
        {
            Id = $"{leftDockId}-root",
            VisibleDockables = new ObservableCollection<IDockable> { proportional },
            ActiveDockable = proportional,
            DefaultDockable = proportional,
        };
        proportional.Owner = root;

        var serializer = new DockSerializer(typeof(ObservableCollection<>), new WorkspaceDockTypeInfoResolver());
        return serializer.Serialize<global::Dock.Model.Controls.IRootDock>(root);
    }

    /// <summary>Recursively finds a dock by Id within a dockable tree.</summary>
    public static IDock? FindDockById(IDockable dockable, string id)
    {
        if (dockable is IDock dock)
        {
            if (string.Equals(dock.Id, id, System.StringComparison.Ordinal))
            {
                return dock;
            }

            if (dock.VisibleDockables is not null)
            {
                foreach (var child in dock.VisibleDockables)
                {
                    var match = FindDockById(child, id);
                    if (match is not null)
                    {
                        return match;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Enumerates every dock in the tree.</summary>
    public static IEnumerable<IDock> EnumerateDocks(IDockable dockable)
    {
        if (dockable is IDock dock)
        {
            yield return dock;
            if (dock.VisibleDockables is not null)
            {
                foreach (var child in dock.VisibleDockables)
                {
                    foreach (var nested in EnumerateDocks(child))
                    {
                        yield return nested;
                    }
                }
            }
        }
    }

    public static WorkspaceDockFactory GetDockFactory(MainWindowViewModel viewModel)
    {
        var field = typeof(MainWindowViewModel).GetField("dockFactory", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (WorkspaceDockFactory)field.GetValue(viewModel)!;
    }

    /// <summary>
    /// Awaits (event-based, no polling) until <paramref name="dock"/> holds at least
    /// <paramref name="expectedCount"/> visible dockables. Returns immediately if already met.
    /// </summary>
    public static async Task WaitForDockableCountAsync(IDock dock, int expectedCount)
    {
        if ((dock.VisibleDockables?.Count ?? 0) >= expectedCount)
        {
            return;
        }

        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if ((dock.VisibleDockables?.Count ?? 0) >= expectedCount)
            {
                signal.TrySetResult();
            }
        }

        if (dock.VisibleDockables is INotifyCollectionChanged observable)
        {
            observable.CollectionChanged += OnChanged;
            try
            {
                if ((dock.VisibleDockables?.Count ?? 0) < expectedCount)
                {
                    await signal.Task;
                }
            }
            finally
            {
                observable.CollectionChanged -= OnChanged;
            }
        }
    }
}
