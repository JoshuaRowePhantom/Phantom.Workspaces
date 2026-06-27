using System;
using System.Windows.Input;
using Phantom.Workspaces.Services.Notifications;

namespace Phantom.Workspaces.ViewModels;

public sealed class NotificationRowViewModel : ViewModelBase
{
    private bool isRead;
    private bool isSnoozed;

    public NotificationRowViewModel(
        NotificationEntry entry,
        ICommand navigateCommand,
        ICommand snoozeCommand)
    {
        this.TabKey = entry.TabKey;
        this.TabTitle = entry.TabDescriptor.TabTitle ?? entry.TabDescriptor.TabId;
        this.Reason = entry.Reason ?? string.Empty;
        this.Timestamp = entry.Timestamp;
        this.isRead = entry.IsRead;
        this.isSnoozed = entry.IsSnoozed;
        this.NavigateCommand = navigateCommand;
        this.SnoozeCommand = snoozeCommand;
    }

    public string TabKey { get; }
    public string TabTitle { get; }
    public string Reason { get; }
    public bool HasReason => !string.IsNullOrEmpty(this.Reason);
    public DateTimeOffset Timestamp { get; }

    public bool IsRead
    {
        get => this.isRead;
        set => this.SetProperty(ref this.isRead, value);
    }

    public bool IsSnoozed
    {
        get => this.isSnoozed;
        set => this.SetProperty(ref this.isSnoozed, value);
    }

    public string RelativeTime
    {
        get
        {
            var elapsed = DateTimeOffset.UtcNow - this.Timestamp;
            if (elapsed.TotalSeconds < 60) return "just now";
            if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes} min ago";
            if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours}h ago";
            return $"{(int)elapsed.TotalDays}d ago";
        }
    }

    public ICommand NavigateCommand { get; }
    public ICommand SnoozeCommand { get; }
}
