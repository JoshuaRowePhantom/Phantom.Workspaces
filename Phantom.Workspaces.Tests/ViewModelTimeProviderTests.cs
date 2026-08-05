using System;
using System.Windows.Input;
using Microsoft.Extensions.Time.Testing;
using Phantom.Workspaces.Services.Notifications;
using Phantom.Workspaces.ViewModels;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class ViewModelTimeProviderTests
{
    private sealed class NullCommand : ICommand
    {
        public static readonly NullCommand Instance = new();
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => false;
        public void Execute(object? parameter) { }
    }

    private static NotificationRowViewModel MakeRow(DateTime when, TimeProvider timeProvider) =>
        new NotificationRowViewModel(
            new NotificationEntry
            {
                TabKey = "tab-1",
                TabDescriptor = new TabDescriptor { TabId = "Tab 1", TabTitle = null },
                Heading = "Completed",
                Description = "done",
                When = when,
                IsRunning = false,
                IsInteresting = false,
                IsRead = false,
                IsSnoozed = false,
            },
            NullCommand.Instance,
            NullCommand.Instance,
            timeProvider);

    [Fact]
    public void RelativeTime_WhenLessThanAMinuteElapsed_ReturnsJustNow()
    {
        var now = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var fake = new FakeTimeProvider(now);
        var row = MakeRow(now.UtcDateTime.AddSeconds(-30), fake);

        Assert.Equal("just now", row.RelativeTime);
    }

    [Fact]
    public void RelativeTime_UsesInjectedTimeProvider_ForElapsedComputation()
    {
        var now = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var fake = new FakeTimeProvider(now);
        var row = MakeRow(now.UtcDateTime.AddMinutes(-5), fake);

        Assert.Equal("5 min ago", row.RelativeTime);

        fake.Advance(TimeSpan.FromHours(2));
        Assert.Equal("2h ago", row.RelativeTime);
    }

    [Fact]
    public void RunningAgentRowViewModel_LastActivityAt_InitializedFromTimeProvider()
    {
        var now = new DateTimeOffset(2024, 3, 15, 8, 30, 0, TimeSpan.Zero);
        var fake = new FakeTimeProvider(now);

        var row = new RunningAgentRowViewModel(
            "session-1",
            "entity-1",
            NullCommand.Instance,
            fake);

        Assert.Equal(now.UtcDateTime, row.LastActivityAt);
    }

    [Fact]
    public void RunningAgentRowViewModel_OpenTabCtor_LastActivityAt_InitializedFromTimeProvider()
    {
        var now = new DateTimeOffset(2024, 3, 15, 8, 30, 0, TimeSpan.Zero);
        var fake = new FakeTimeProvider(now);

        var row = new RunningAgentRowViewModel(
            "session-1",
            "Pane",
            "Tab",
            isThinking: true,
            NullCommand.Instance,
            fake);

        Assert.Equal(now.UtcDateTime, row.LastActivityAt);
    }
}
