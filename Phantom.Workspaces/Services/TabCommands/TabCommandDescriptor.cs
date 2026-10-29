namespace Phantom.Workspaces.Services.TabCommands;

public sealed class TabCommandDescriptor
{
    public required string Id { get; init; }

    public required string Header { get; init; }

    public string? AccessibleLabel { get; init; }

    public string Group { get; init; } = "default";

    public int Order { get; init; }

    public Func<TabCommandContext, TabCommandAvailability>? Availability { get; init; }

    public required Func<TabCommandContext, CancellationToken, Task<TabCommandResult>> ExecuteAsync { get; init; }
}

public readonly record struct TabCommandAvailability
{
    public static TabCommandAvailability Hidden { get; } = new()
    {
        IsVisible = false,
        IsEnabled = false,
    };

    public static TabCommandAvailability Enabled { get; } = new()
    {
        IsVisible = true,
        IsEnabled = true,
    };

    public required bool IsVisible { get; init; }

    public required bool IsEnabled { get; init; }

    public string? DisabledReason { get; init; }

    public static TabCommandAvailability Disabled(string reason) => new()
    {
        IsVisible = true,
        IsEnabled = false,
        DisabledReason = reason,
    };
}

public enum TabCommandResultKind
{
    Succeeded,
    Cancelled,
    Rejected,
    Failed,
    AlreadyRunning,
}

public readonly record struct TabCommandResult
{
    public required TabCommandResultKind Kind { get; init; }

    public string? Message { get; init; }

    public Exception? Exception { get; init; }

    public static TabCommandResult Success() => new() { Kind = TabCommandResultKind.Succeeded };

    public static TabCommandResult Cancelled() => new() { Kind = TabCommandResultKind.Cancelled };

    public static TabCommandResult Rejected(string message) =>
        new() { Kind = TabCommandResultKind.Rejected, Message = message };

    public static TabCommandResult Failed(Exception exception) =>
        new() { Kind = TabCommandResultKind.Failed, Exception = exception, Message = exception.Message };

    public static TabCommandResult AlreadyRunning() =>
        new() { Kind = TabCommandResultKind.AlreadyRunning };
}
