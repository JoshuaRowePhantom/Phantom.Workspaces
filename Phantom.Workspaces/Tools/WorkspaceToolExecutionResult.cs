namespace Phantom.Workspaces.Tools;

public sealed record WorkspaceToolExecutionResult
{
    public static WorkspaceToolExecutionResult Success() => new();

    public static WorkspaceToolExecutionResult Failure(string errorMessage) =>
        new() { IsSuccess = false, ErrorMessage = errorMessage };

    public bool IsSuccess { get; init; } = true;

    public string? ErrorMessage { get; init; }
}
