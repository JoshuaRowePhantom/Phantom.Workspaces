using System;
using System.Collections.Generic;

namespace Phantom.Workspaces.Services.Navigation;

public interface INavigationHistoryService
{
    void Push(NavigationEntry entry);

    bool GoBack(out NavigationEntry? entry);

    bool GoForward(out NavigationEntry? entry);

    /// <summary>
    /// Steps backward through history, skipping entries where <paramref name="isEntryAvailable"/> returns <see langword="false"/>,
    /// and stops at the first entry for which it returns <see langword="true"/>.
    /// Returns <see langword="false"/> if all remaining backward entries are unavailable.
    /// </summary>
    bool GoBackSkipping(Func<NavigationEntry, bool> isEntryAvailable, out NavigationEntry? entry);

    /// <summary>
    /// Steps forward through history, skipping entries where <paramref name="isEntryAvailable"/> returns <see langword="false"/>,
    /// and stops at the first entry for which it returns <see langword="true"/>.
    /// Returns <see langword="false"/> if all remaining forward entries are unavailable.
    /// </summary>
    bool GoForwardSkipping(Func<NavigationEntry, bool> isEntryAvailable, out NavigationEntry? entry);

    /// <summary>Directly set the current position in the history list by index.</summary>
    bool GoToIndex(int index, out NavigationEntry? entry);

    bool CanGoBack { get; }

    bool CanGoForward { get; }

    IReadOnlyList<NavigationEntry> Entries { get; }

    int CurrentIndex { get; }

    event EventHandler CanNavigateChanged;
}
