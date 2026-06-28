using System;
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
        bool isRunning = false) =>
        new NotificationRowViewModel(
            new NotificationEntry
            {
                TabKey = "tab-1",
                TabDescriptor = new TabDescriptor { TabId = "Tab 1", TabTitle = tabTitle },
                Heading = heading,
                Description = description,
                When = DateTime.UtcNow,
                IsRunning = isRunning,
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
}

