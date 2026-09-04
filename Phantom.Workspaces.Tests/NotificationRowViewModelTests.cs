using System;
using System.Collections.Generic;
using System.Windows.Input;
using Phantom.Workspaces.Services.Notifications;
using Phantom.Workspaces.ViewModels;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class NotificationRowViewModelTests
{
    private sealed class NullCommand : ICommand
    {
        public static readonly NullCommand Instance = new();
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => false;
        public void Execute(object? parameter) { }
    }

    private static NotificationRowViewModel MakeRow(
        string description,
        string heading = "Completed",
        string? tabTitle = null,
        bool isRunning = false,
        bool isInteresting = false) =>
        new NotificationRowViewModel(
            new NotificationEntry
            {
                TabKey = "tab-1",
                TabDescriptor = new TabDescriptor { TabId = "Tab 1", TabTitle = tabTitle },
                Heading = heading,
                Description = description,
                When = DateTime.UtcNow,
                IsRunning = isRunning,
                IsInteresting = isInteresting,
                IsRead = false,
                IsSnoozed = false,
            },
            NullCommand.Instance,
            NullCommand.Instance);

    [Fact]
    public void HasDescription_WhenDescriptionIsNonEmpty_ReturnsTrue()
    {
        var row = MakeRow("Filed as #123");
        Assert.True(row.HasDescription);
    }

    [Fact]
    public void HasDescription_WhenDescriptionIsEmpty_ReturnsFalse()
    {
        var row = MakeRow(string.Empty);
        Assert.False(row.HasDescription);
    }

    [Fact]
    public void Description_WhenSet_IsReturned()
    {
        var row = MakeRow("initial");
        Assert.Equal("initial", row.Description);
    }

    [Fact]
    public void Heading_WhenSet_IsReturned()
    {
        var row = MakeRow("desc", heading: "Running");
        Assert.Equal("Running", row.Heading);
    }

    [Fact]
    public void IsRunning_WhenEntryIsRunning_ReturnsTrue()
    {
        var row = MakeRow("live summary", isRunning: true);
        Assert.True(row.IsRunning);
    }

    [Fact]
    public void IsRunning_WhenEntryIsNotRunning_ReturnsFalse()
    {
        var row = MakeRow("completed reason", isRunning: false);
        Assert.False(row.IsRunning);
    }

    [Fact]
    public void TabTitle_UsesTabTitleFromDescriptor_WhenTabTitleIsSet()
    {
        var row = MakeRow("reason", tabTitle: "My Full Agent Title");
        Assert.Equal("My Full Agent Title", row.TabTitle);
    }

    [Fact]
    public void TabTitle_FallsBackToTabId_WhenTabTitleIsNull()
    {
        var row = MakeRow("reason", tabTitle: null);
        Assert.Equal("Tab 1", row.TabTitle);
    }

    [Fact]
    public void IsInteresting_WhenNotificationStateIsInteresting_ReturnsTrue()
    {
        var row = MakeRow("done", isInteresting: true);
        Assert.True(row.IsInteresting);
    }

    [Fact]
    public void IsInteresting_WhenNotificationStateIsNotInteresting_ReturnsFalse()
    {
        var row = MakeRow("live summary", isInteresting: false);
        Assert.False(row.IsInteresting);
    }

    [Fact]
    public void IsRunningAndIsInteresting_BothTrueSimultaneously_BothPropertiesTrue()
    {
        var row = MakeRow("live summary", isRunning: true, isInteresting: true);
        Assert.True(row.IsRunning);
        Assert.True(row.IsInteresting);
    }

    [Fact]
    public void Status_WhenEntryIsRunning_RunningStatusIsRunning()
    {
        var row = MakeRow("live", isRunning: true);
        Assert.Equal(RunningStatus.Running, row.Status.RunningStatus);
    }

    [Fact]
    public void Status_WhenEntryIsNotRunning_RunningStatusIsIdle()
    {
        var row = MakeRow("done", isRunning: false);
        Assert.Equal(RunningStatus.Idle, row.Status.RunningStatus);
    }

    [Fact]
    public void Status_WhenEntryIsInteresting_ErrorStatusIsError()
    {
        var row = MakeRow("done", isInteresting: true);
        Assert.Equal(ErrorStatus.Error, row.Status.ErrorStatus);
    }

    [Fact]
    public void Status_WhenEntryIsNotInteresting_ErrorStatusIsNone()
    {
        var row = MakeRow("done", isInteresting: false);
        Assert.Equal(ErrorStatus.None, row.Status.ErrorStatus);
    }

    [Fact]
    public void ShowsAttentionIndicator_WhenInterestingAndUnread_IsTrue()
    {
        var row = MakeRow("done", isInteresting: true);
        Assert.False(row.IsRead);
        Assert.True(row.ShowsAttentionIndicator);
    }

    [Fact]
    public void ShowsAttentionIndicator_WhenNotInteresting_IsFalse()
    {
        var row = MakeRow("done", isInteresting: false);
        Assert.False(row.ShowsAttentionIndicator);
    }

    [Fact]
    public void ShowsAttentionIndicator_WhenIsReadFlipsTrue_BecomesFalse()
    {
        var row = MakeRow("done", isInteresting: true);
        Assert.True(row.ShowsAttentionIndicator);

        row.IsRead = true;

        Assert.False(row.ShowsAttentionIndicator);
    }

    [Fact]
    public void ShowsAttentionIndicator_RaisesPropertyChanged_WhenIsReadChanges()
    {
        var row = MakeRow("done", isInteresting: true);
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        row.IsRead = true;

        Assert.Contains(nameof(NotificationRowViewModel.ShowsAttentionIndicator), raised);
    }

    [Fact]
    public void ShowsAttentionIndicator_RaisesPropertyChanged_WhenIsInterestingChanges()
    {
        var row = MakeRow("done", isInteresting: false);
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        row.IsInteresting = true;

        Assert.Contains(nameof(NotificationRowViewModel.ShowsAttentionIndicator), raised);
        Assert.True(row.ShowsAttentionIndicator);
    }
}

