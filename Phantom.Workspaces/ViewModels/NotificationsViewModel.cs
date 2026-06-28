using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Phantom.Workspaces.Services.Notifications;

namespace Phantom.Workspaces.ViewModels;

public sealed class NotificationsViewModel : TransientPopupViewModel, IDisposable
{
    private readonly NotificationService notificationService;
    private readonly Action<string> navigateToTab;
    private int lastKnownUnreadCount;

    public NotificationsViewModel(NotificationService notificationService, Action<string> navigateToTab)
    {
        this.notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        this.navigateToTab = navigateToTab ?? throw new ArgumentNullException(nameof(navigateToTab));
        this.ToggleOpenCommand = new RelayCommand(_ => this.ToggleOpen());
        this.notificationService.NotificationsChanged += this.OnNotificationsChanged;
        this.Rows = new ObservableCollection<NotificationRowViewModel>();
        this.RefreshRows();
        this.lastKnownUnreadCount = this.UnreadCount;
    }

    public ObservableCollection<NotificationRowViewModel> Rows { get; }

    public ICommand ToggleOpenCommand { get; }

    public int UnreadCount => this.notificationService.Notifications.Count(e => !e.IsRead);

    public bool HasUnread => this.UnreadCount > 0;

    public bool HasRows => this.Rows.Count > 0;

    public bool HasActiveRun => this.notificationService.HasActiveRun;

    public void ToggleOpen()
    {
        this.IsOpen = !this.IsOpen;
        this.IsAutoClosing = false;
    }

    public void OpenWithHighlight(string tabKey)
    {
        foreach (var row in this.Rows)
        {
            row.IsHighlighted = false;
        }

        var target = this.Rows.FirstOrDefault(r => r.TabKey == tabKey);
        if (target is not null)
        {
            target.IsHighlighted = true;
        }

        this.Show();
    }

    protected override void OnDismissed()
    {
        foreach (var row in this.Rows)
        {
            row.IsHighlighted = false;
        }

        this.RefreshRows();
    }

    private void OnNotificationsChanged(object? sender, EventArgs e)
    {
        var previousUnreadCount = this.lastKnownUnreadCount;
        this.RefreshRows();
        this.lastKnownUnreadCount = this.UnreadCount;

        if (this.UnreadCount > previousUnreadCount)
        {
            this.Show();
        }
    }

    private void RefreshRows()
    {
        if (this.IsOpen)
        {
            this.RefreshRowsInPlace();
        }
        else
        {
            this.RefreshRowsFull();
        }

        this.RaisePropertyChanged(nameof(this.UnreadCount));
        this.RaisePropertyChanged(nameof(this.HasUnread));
        this.RaisePropertyChanged(nameof(this.HasRows));
        this.RaisePropertyChanged(nameof(this.HasActiveRun));
    }

    private void RefreshRowsFull()
    {
        this.Rows.Clear();
        var sorted = this.notificationService.Notifications
            .OrderBy(e => e.IsRead)
            .ThenByDescending(e => e.When);

        foreach (var entry in sorted)
        {
            this.Rows.Add(this.CreateRow(entry));
        }
    }

    private void RefreshRowsInPlace()
    {
        var notificationsMap = this.notificationService.Notifications.ToDictionary(e => e.TabKey);

        // Update existing rows in-place without reordering
        foreach (var row in this.Rows)
        {
            if (notificationsMap.TryGetValue(row.TabKey, out var entry))
            {
                row.Heading = entry.Heading;
                row.Description = entry.Description;
                row.When = entry.When;
                row.IsRunning = entry.IsRunning;
                row.IsInteresting = entry.IsInteresting;
                row.IsRead = entry.IsRead;
                row.IsSnoozed = entry.IsSnoozed;
            }
        }

        // Prepend new notifications (not yet in Rows) at the top
        var existingTabKeys = this.Rows.Select(r => r.TabKey).ToHashSet();
        var newEntries = this.notificationService.Notifications
            .Where(e => !existingTabKeys.Contains(e.TabKey))
            .OrderByDescending(e => e.When);

        int insertIndex = 0;
        foreach (var entry in newEntries)
        {
            this.Rows.Insert(insertIndex++, this.CreateRow(entry));
        }
    }

    private NotificationRowViewModel CreateRow(NotificationEntry entry)
    {
        var tabKey = entry.TabKey;
        var navigateCmd = new RelayCommand(_ =>
        {
            this.IsOpen = false;
            this.notificationService.MarkRead(tabKey);
            this.navigateToTab(tabKey);
        });
        var snoozeCmd = new RelayCommand(_ =>
        {
            if (this.notificationService.IsTabSnoozed(tabKey))
            {
                this.notificationService.UnsnoozeTab(tabKey);
            }
            else
            {
                this.notificationService.SnoozeTab(tabKey);
            }
        });
        return new NotificationRowViewModel(entry, navigateCmd, snoozeCmd);
    }

    public void Dispose()
    {
        this.notificationService.NotificationsChanged -= this.OnNotificationsChanged;
    }
}
