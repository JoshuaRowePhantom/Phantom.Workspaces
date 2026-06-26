using System;
using Phantom.Workspaces.Services.Navigation;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class NavigationStackTests
{
    private static NavigationEntry Entry(string tabId, string? paneId = null) =>
        new NavigationEntry(tabId, paneId);

    [Fact]
    public void NavigationStack_AtStart_CannotGoBack()
    {
        var service = new NavigationHistoryService();

        Assert.False(service.CanGoBack);
    }

    [Fact]
    public void NavigationStack_AtEnd_CannotGoForward()
    {
        var service = new NavigationHistoryService();
        service.Push(Entry("tab-a"));
        service.Push(Entry("tab-b"));

        Assert.False(service.CanGoForward);
    }

    [Fact]
    public void NavigationStack_GoBack_ReturnsCorrectEntry()
    {
        var service = new NavigationHistoryService();
        service.Push(Entry("tab-a"));
        service.Push(Entry("tab-b"));

        var result = service.GoBack(out var entry);

        Assert.True(result);
        Assert.Equal(Entry("tab-a"), entry);
        Assert.True(service.CanGoForward);
    }

    [Fact]
    public void NavigationStack_GoForward_ReturnsCorrectEntry()
    {
        var service = new NavigationHistoryService();
        service.Push(Entry("tab-a"));
        service.Push(Entry("tab-b"));
        service.GoBack(out _);

        var result = service.GoForward(out var entry);

        Assert.True(result);
        Assert.Equal(Entry("tab-b"), entry);
        Assert.False(service.CanGoForward);
    }

    [Fact]
    public void NavigationStack_Push_ClearsForwardHistory()
    {
        var service = new NavigationHistoryService();
        service.Push(Entry("tab-a"));
        service.Push(Entry("tab-b"));
        service.Push(Entry("tab-c"));

        // Go back twice so forward history contains tab-b and tab-c
        service.GoBack(out _);
        service.GoBack(out _);
        Assert.True(service.CanGoForward);

        // Push a new entry — forward history should be cleared
        service.Push(Entry("tab-d"));

        Assert.False(service.CanGoForward);
        service.GoBack(out var prev);
        Assert.Equal(Entry("tab-a"), prev);
    }
}
