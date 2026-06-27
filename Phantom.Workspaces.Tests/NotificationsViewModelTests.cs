using System;
using System.Collections.Generic;
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
    public void OnNotificationsChanged_WhenNotificationArrives_SetsIsOpenTrue()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, _ => { });

        service.Notify(Tab("tab-1"), "Test notification");

        Assert.True(viewModel.IsOpen);
    }

    [Fact]
    public void OnNotificationsChanged_WhenNotificationArrives_SetsIsAutoClosingTrue()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, _ => { });

        service.Notify(Tab("tab-1"), "Test notification");

        Assert.True(viewModel.IsAutoClosing);
    }

    [Fact]
    public void OnNotificationsChanged_RaisesPropertyChangedForIsAutoClosing()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, _ => { });

        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        service.Notify(Tab("tab-1"), "Test notification");

        Assert.Contains(nameof(viewModel.IsAutoClosing), changedProperties);
    }

    [Fact]
    public void ToggleOpen_WhenCalledWhileOpen_ClosesPopup()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, _ => { });
        service.Notify(Tab("tab-1"), "notification");  // opens it

        viewModel.ToggleOpen();

        Assert.False(viewModel.IsOpen);
    }

    [Fact]
    public void ToggleOpen_SetsIsAutoClosingFalse()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, _ => { });
        service.Notify(Tab("tab-1"), "notification");  // sets IsAutoClosing = true

        viewModel.ToggleOpen();

        Assert.False(viewModel.IsAutoClosing);
    }

    [Fact]
    public void ToggleOpen_RaisesPropertyChangedForIsAutoClosing()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, _ => { });

        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        viewModel.ToggleOpen();

        Assert.Contains(nameof(viewModel.IsAutoClosing), changedProperties);
    }
}
