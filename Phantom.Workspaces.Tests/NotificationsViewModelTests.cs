using System;
using System.Linq;
using Phantom.Workspaces.Services.Notifications;
using Phantom.Workspaces.ViewModels;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class NotificationsViewModelTests
{
    private sealed class FakeActiveTabProvider : IActiveTabProvider
    {
        public string? ActiveTabId { get; set; }
    }

    private static TabDescriptor Tab(string tabId) =>
        new TabDescriptor { TabId = tabId };

    [Fact]
    public void HasRows_WithNoRows_ReturnsFalse()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, _ => { });

        Assert.False(viewModel.HasRows);
    }

    [Fact]
    public void HasRows_WithRows_ReturnsTrue()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, _ => { });
        
        service.Notify(Tab("tab-1"), "Test notification");

        Assert.True(viewModel.HasRows);
    }

    [Fact]
    public void HasRows_RaisesPropertyChanged_WhenRowsChange()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, _ => { });
        
        bool hasRowsChanged = false;
        viewModel.PropertyChanged += (sender, args) =>
        {
            if (args.PropertyName == nameof(viewModel.HasRows))
            {
                hasRowsChanged = true;
            }
        };

        service.Notify(Tab("tab-1"), "Test notification");

        Assert.True(hasRowsChanged);
        Assert.True(viewModel.HasRows);
    }

    [Fact]
    public void HasActiveRun_WhenServiceHasActiveRun_ReturnsTrue()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, _ => { });

        service.NotifyRunning("tab-1", true);

        Assert.True(viewModel.HasActiveRun);
    }

    [Fact]
    public void HasActiveRun_WhenNoRunActive_ReturnsFalse()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, _ => { });

        Assert.False(viewModel.HasActiveRun);
    }

    [Fact]
    public void HasActiveRun_RaisesPropertyChanged_WhenRunStarts()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, _ => { });

        bool hasActiveRunChanged = false;
        viewModel.PropertyChanged += (sender, args) =>
        {
            if (args.PropertyName == nameof(viewModel.HasActiveRun))
            {
                hasActiveRunChanged = true;
            }
        };

        service.NotifyRunning("tab-1", true);

        Assert.True(hasActiveRunChanged);
    }

    [Fact]
    public void HasActiveRun_RaisesPropertyChanged_WhenRunStops()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, _ => { });
        service.NotifyRunning("tab-1", true);

        bool hasActiveRunChanged = false;
        viewModel.PropertyChanged += (sender, args) =>
        {
            if (args.PropertyName == nameof(viewModel.HasActiveRun))
            {
                hasActiveRunChanged = true;
            }
        };

        service.NotifyRunning("tab-1", false);

        Assert.True(hasActiveRunChanged);
    }
}
