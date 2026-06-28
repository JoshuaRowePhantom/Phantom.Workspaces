using System;
using System.Collections.Generic;
using Phantom.Workspaces.Services.Navigation;
using Phantom.Workspaces.ViewModels;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class NavigationStackPopupViewModelTests
{
    private static NavigationEntry Entry(string tabId) => new NavigationEntry(tabId, null);

    private static string? DefaultTitleProvider(string tabId) => tabId + "-title";

    private static NavigationStackPopupViewModel CreateViewModel(
        INavigationHistoryService service,
        Func<string, string?>? titleProvider = null)
        => new NavigationStackPopupViewModel(service, titleProvider ?? DefaultTitleProvider);

    [Fact]
    public void OpenAtCurrentPosition_WithEntries_SetsIsOpenTrue_WithoutAutoClose()
    {
        var service = new NavigationHistoryService();
        service.Push(Entry("tab-a"));
        service.Push(Entry("tab-b"));
        var vm = CreateViewModel(service);

        vm.OpenAtCurrentPosition();

        Assert.True(vm.IsOpen);
        Assert.False(vm.IsAutoClosing);
    }

    [Fact]
    public void OpenAtCurrentPosition_WithEntries_RowsMostRecentFirst()
    {
        var service = new NavigationHistoryService();
        service.Push(Entry("tab-a"));
        service.Push(Entry("tab-b"));
        service.Push(Entry("tab-c"));
        var vm = CreateViewModel(service);

        vm.OpenAtCurrentPosition();

        Assert.Equal(3, vm.Rows.Count);
        Assert.Equal("tab-c-title", vm.Rows[0].TabTitle);
        Assert.Equal("tab-b-title", vm.Rows[1].TabTitle);
        Assert.Equal("tab-a-title", vm.Rows[2].TabTitle);
    }

    [Fact]
    public void OpenAtCurrentPosition_WithNoEntries_HasNoRows()
    {
        var service = new NavigationHistoryService();
        var vm = CreateViewModel(service);

        vm.OpenAtCurrentPosition();

        Assert.Empty(vm.Rows);
    }

    [Fact]
    public void OpenAtCurrentPosition_SetsSelectedIndexToCurrentEntry()
    {
        var service = new NavigationHistoryService();
        service.Push(Entry("tab-a"));
        service.Push(Entry("tab-b"));
        service.Push(Entry("tab-c"));
        // currentIndex = 2 (tab-c). Display: row 0=tab-c, row 1=tab-b, row 2=tab-a.
        // selectedDisplayIndex = 3-1-2 = 0
        var vm = CreateViewModel(service);

        vm.OpenAtCurrentPosition();

        Assert.Equal(0, vm.SelectedIndex);
    }

    [Fact]
    public void OpenAtCurrentPosition_AfterGoBack_SetsSelectedIndexToBackEntry()
    {
        var service = new NavigationHistoryService();
        service.Push(Entry("tab-a"));
        service.Push(Entry("tab-b"));
        service.Push(Entry("tab-c"));
        service.GoBack(out _); // currentIndex = 1 (tab-b). displayIndex = 3-1-1 = 1
        var vm = CreateViewModel(service);

        vm.OpenAtCurrentPosition();

        Assert.Equal(1, vm.SelectedIndex);
    }

    [Fact]
    public void MoveSelectionUp_DecreasesSelectedIndex()
    {
        var service = new NavigationHistoryService();
        service.Push(Entry("tab-a"));
        service.Push(Entry("tab-b"));
        service.Push(Entry("tab-c"));
        service.GoBack(out _); // selectedIndex = 1
        var vm = CreateViewModel(service);
        vm.OpenAtCurrentPosition();
        Assert.Equal(1, vm.SelectedIndex);

        vm.MoveSelectionUp();

        Assert.Equal(0, vm.SelectedIndex);
    }

    [Fact]
    public void MoveSelectionDown_IncreasesSelectedIndex()
    {
        var service = new NavigationHistoryService();
        service.Push(Entry("tab-a"));
        service.Push(Entry("tab-b"));
        service.Push(Entry("tab-c"));
        service.GoBack(out _); // selectedIndex = 1
        var vm = CreateViewModel(service);
        vm.OpenAtCurrentPosition();

        vm.MoveSelectionDown();

        Assert.Equal(2, vm.SelectedIndex);
    }

    [Fact]
    public void MoveSelectionUp_AtTopRow_DoesNotGoNegative()
    {
        var service = new NavigationHistoryService();
        service.Push(Entry("tab-a"));
        service.Push(Entry("tab-b"));
        var vm = CreateViewModel(service);
        vm.OpenAtCurrentPosition(); // selectedIndex = 0 (most recent)

        vm.MoveSelectionUp();

        Assert.Equal(0, vm.SelectedIndex);
    }

    [Fact]
    public void MoveSelectionDown_AtBottomRow_DoesNotExceedMax()
    {
        var service = new NavigationHistoryService();
        service.Push(Entry("tab-a"));
        service.Push(Entry("tab-b"));
        var vm = CreateViewModel(service);
        vm.OpenAtCurrentPosition();
        // Move to the bottom
        vm.MoveSelectionDown(); // index 1 (last row for 2 entries)

        vm.MoveSelectionDown();

        Assert.Equal(1, vm.SelectedIndex);
    }

    [Fact]
    public void CommitAndBeginFade_WhenSelectionUnchanged_ReturnsNoNavigationNeeded()
    {
        var service = new NavigationHistoryService();
        service.Push(Entry("tab-a"));
        service.Push(Entry("tab-b"));
        var vm = CreateViewModel(service);
        vm.OpenAtCurrentPosition(); // selectedIndex = 0 = currentIndex=1

        var result = vm.CommitAndBeginFade();

        Assert.Equal(-1, result);
    }

    [Fact]
    public void CommitAndBeginFade_WhenSelectionMoved_ReturnsCorrectHistoryIndex()
    {
        var service = new NavigationHistoryService();
        service.Push(Entry("tab-a")); // history[0]
        service.Push(Entry("tab-b")); // history[1]
        service.Push(Entry("tab-c")); // history[2], currentIndex=2
        // Display: row0=tab-c, row1=tab-b, row2=tab-a. selectedIndex=0.
        var vm = CreateViewModel(service);
        vm.OpenAtCurrentPosition();
        vm.MoveSelectionDown(); // selectedIndex=1 → tab-b → historyIndex=1

        var result = vm.CommitAndBeginFade();

        Assert.Equal(1, result);
    }

    [Fact]
    public void CommitAndBeginFade_SetsIsAutoClosingTrue()
    {
        var service = new NavigationHistoryService();
        service.Push(Entry("tab-a"));
        var vm = CreateViewModel(service);
        vm.OpenAtCurrentPosition();

        vm.CommitAndBeginFade();

        Assert.True(vm.IsAutoClosing);
    }

    [Fact]
    public void HoldDuration_Is500ms()
    {
        var service = new NavigationHistoryService();
        var vm = CreateViewModel(service);
        Assert.Equal(TimeSpan.FromMilliseconds(500), vm.HoldDuration);
    }

    [Fact]
    public void FadeDuration_Is500ms()
    {
        var service = new NavigationHistoryService();
        var vm = CreateViewModel(service);
        Assert.Equal(TimeSpan.FromMilliseconds(500), vm.FadeDuration);
    }

    [Fact]
    public void OnCanNavigateChanged_WhenOpen_RefreshesRows()
    {
        var service = new NavigationHistoryService();
        service.Push(Entry("tab-a"));
        var vm = CreateViewModel(service);
        vm.OpenAtCurrentPosition();
        Assert.Single(vm.Rows);

        // Navigate adds a new entry → CanNavigateChanged fires → rows refresh
        service.Push(Entry("tab-b"));

        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal("tab-b-title", vm.Rows[0].TabTitle);
    }

    [Fact]
    public void OnCanNavigateChanged_WhenClosed_DoesNotRefreshRows()
    {
        var service = new NavigationHistoryService();
        service.Push(Entry("tab-a"));
        var vm = CreateViewModel(service);
        // Do NOT open popup

        service.Push(Entry("tab-b"));

        Assert.Empty(vm.Rows);
    }
}
