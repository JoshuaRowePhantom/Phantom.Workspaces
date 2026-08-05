using System;
using System.Windows.Input;
using Phantom.Workspaces.Services.Notifications;

namespace Phantom.Workspaces.ViewModels;

public sealed class NotificationRowViewModel : ViewModelBase
{
    private bool isRead;
    private bool isSnoozed;
    private bool isHighlighted;
    private string heading;
    private string description;
    private DateTime when;
    private readonly StatusItem status = new();
    private readonly TimeProvider timeProvider;

    public NotificationRowViewModel(
        NotificationEntry entry,
        ICommand navigateCommand,
        ICommand snoozeCommand,
        TimeProvider? timeProvider = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.TabKey = entry.TabKey;
        this.TabTitle = entry.TabDescriptor.TabTitle ?? entry.TabDescriptor.TabId;
        this.heading = entry.Heading;
        this.description = entry.Description;
        this.when = entry.When;
        this.status.RunningStatus = entry.IsRunning ? RunningStatus.Running : RunningStatus.Idle;
        this.status.ErrorStatus = entry.IsInteresting ? ErrorStatus.Error : ErrorStatus.None;
        this.isRead = entry.IsRead;
        this.isSnoozed = entry.IsSnoozed;
        this.NavigateCommand = navigateCommand;
        this.SnoozeCommand = snoozeCommand;
    }

    public string TabKey { get; }
    public string TabTitle { get; }

    public IStatusItem Status => this.status;

    public string Heading
    {
        get => this.heading;
        set => this.SetProperty(ref this.heading, value);
    }

    public string Description
    {
        get => this.description;
        set
        {
            if (this.SetProperty(ref this.description, value))
            {
                this.RaisePropertyChanged(nameof(this.HasDescription));
            }
        }
    }

    public bool HasDescription => !string.IsNullOrEmpty(this.Description);

    public bool IsRunning
    {
        get => this.status.RunningStatus == RunningStatus.Running;
        set
        {
            var newStatus = value ? RunningStatus.Running : RunningStatus.Idle;
            if (this.status.RunningStatus != newStatus)
            {
                this.status.RunningStatus = newStatus;
                this.RaisePropertyChanged();
            }
        }
    }

    public bool IsInteresting
    {
        get => this.status.ErrorStatus == ErrorStatus.Error;
        set
        {
            var newError = value ? ErrorStatus.Error : ErrorStatus.None;
            if (this.status.ErrorStatus != newError)
            {
                this.status.ErrorStatus = newError;
                this.RaisePropertyChanged();
            }
        }
    }

    public DateTime When
    {
        get => this.when;
        set => this.SetProperty(ref this.when, value);
    }

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

    public bool IsHighlighted
    {
        get => this.isHighlighted;
        set => this.SetProperty(ref this.isHighlighted, value);
    }

    public string RelativeTime
    {
        get
        {
            var elapsed = this.timeProvider.GetUtcNow().UtcDateTime - this.When;
            if (elapsed.TotalSeconds < 60) return "just now";
            if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes} min ago";
            if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours}h ago";
            return $"{(int)elapsed.TotalDays}d ago";
        }
    }

    public ICommand NavigateCommand { get; }
    public ICommand SnoozeCommand { get; }
}

