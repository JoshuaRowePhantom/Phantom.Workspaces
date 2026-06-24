namespace Phantom.Workspaces.Install;

/// <summary>A user-facing notification (e.g. a tray toast).</summary>
public sealed record Notification
{
    /// <summary>The notification title.</summary>
    public required string Title { get; init; }

    /// <summary>The notification body text.</summary>
    public required string Message { get; init; }

    /// <summary>The notification severity, defaulting to informational.</summary>
    public NotificationKind Kind { get; init; } = NotificationKind.Information;
}

/// <summary>The severity of a <see cref="Notification"/>.</summary>
public enum NotificationKind
{
    /// <summary>Informational (e.g. "an update is available").</summary>
    Information,

    /// <summary>A warning.</summary>
    Warning,

    /// <summary>An error.</summary>
    Error,
}

/// <summary>
/// Wraps tray toasts so update-notification logic is unit-testable against a fake sink: a fresh
/// release raises a toast without touching the real notification area.
/// </summary>
public interface INotifier
{
    /// <summary>Shows <paramref name="notification"/> to the user.</summary>
    void Notify(Notification notification);
}

/// <summary>A no-op <see cref="INotifier"/> for headless modes (e.g. silent install).</summary>
public sealed class NullNotifier : INotifier
{
    /// <summary>A shared instance.</summary>
    public static readonly NullNotifier Instance = new();

    /// <inheritdoc />
    public void Notify(Notification notification)
    {
    }
}
