using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Phantom.Workspaces.Services.Notifications;

namespace Phantom.Workspaces.ViewModels;

public sealed class NotificationsViewModel : ViewModelBase, IDisposable
{
    private readonly NotificationService notificationService;
    private readonly Action<string> navigateToTab;
    private bool isOpen;

    public NotificationsViewModel(NotificationService notificationService, Action<string> navigateToTab)
    {
        this.notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        this.navigateToTab = navigateToTab ?? throw new ArgumentNullException(nameof(navigateToTab));
        this.ToggleOpenCommand = new RelayCommand(_ => this.ToggleOpen());
        this.notificationService.NotificationsChanged += this.OnNotificationsChanged;
        this.Rows = new ObservableCollection<NotificationRowViewModel>();
        this.RefreshRows();
    }

    public ObservableCollection<NotificationRowViewModel> Rows { get; }

    public ICommand ToggleOpenCommand { get; }

    public bool IsOpen
    {
        get => this.isOpen;
        set => this.SetProperty(ref this.isOpen, value);
    }

    public int UnreadCount => this.notificationService.Notifications.Count(e => !e.IsRead);

    public bool HasUnread => this.UnreadCount > 0;

    public void ToggleOpen() => this.IsOpen = !this.IsOpen;

    private void OnNotificationsChanged(object? sender, EventArgs e) => this.RefreshRows();

    private void RefreshRows()
    {
        this.Rows.Clear();
        // Newest first, unread at top
        var sorted = this.notificationService.Notifications
            .OrderBy(e => e.IsRead)
            .ThenByDescending(e => e.Timestamp);

        foreach (var entry in sorted)
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
            this.Rows.Add(new NotificationRowViewModel(entry, navigateCmd, snoozeCmd));
        }

        this.RaisePropertyChanged(nameof(this.UnreadCount));
        this.RaisePropertyChanged(nameof(this.HasUnread));
    }

    public void Dispose()
    {
        this.notificationService.NotificationsChanged -= this.OnNotificationsChanged;
    }
}
