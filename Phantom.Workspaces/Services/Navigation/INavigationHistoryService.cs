using System;
using System.Collections.Generic;

namespace Phantom.Workspaces.Services.Navigation;

public interface INavigationHistoryService
{
    void Push(NavigationEntry entry);

    bool GoBack(out NavigationEntry? entry);

    bool GoForward(out NavigationEntry? entry);

    /// <summary>Directly set the current position in the history list by index.</summary>
    bool GoToIndex(int index, out NavigationEntry? entry);

    bool CanGoBack { get; }

    bool CanGoForward { get; }

    IReadOnlyList<NavigationEntry> Entries { get; }

    int CurrentIndex { get; }

    event EventHandler CanNavigateChanged;
}
