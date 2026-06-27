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

    private static NotificationRowViewModel MakeRow(string? reason) =>
        new NotificationRowViewModel(
            new NotificationEntry
            {
                TabKey = "tab-1",
                TabDescriptor = new TabDescriptor { TabId = "Tab 1" },
                Reason = reason,
                Timestamp = DateTimeOffset.UtcNow,
                IsRead = false,
                IsSnoozed = false,
            },
            NullCommand.Instance,
            NullCommand.Instance);

    [Fact]
    public void HasReason_WhenReasonIsNonEmpty_ReturnsTrue()
    {
        var row = MakeRow("Filed as #123");
        Assert.True(row.HasReason);
    }

    [Fact]
    public void HasReason_WhenReasonIsNull_ReturnsFalse()
    {
        var row = MakeRow(null);
        Assert.False(row.HasReason);
    }

    [Fact]
    public void HasReason_WhenReasonIsEmpty_ReturnsFalse()
    {
        var row = MakeRow(string.Empty);
        Assert.False(row.HasReason);
    }
}
