using System;

namespace Phantom.Workspaces.Services.Navigation;

public interface INavigationHistoryService
{
    void Push(NavigationEntry entry);

    bool GoBack(out NavigationEntry? entry);

    bool GoForward(out NavigationEntry? entry);

    bool CanGoBack { get; }

    bool CanGoForward { get; }

    event EventHandler CanNavigateChanged;
}
