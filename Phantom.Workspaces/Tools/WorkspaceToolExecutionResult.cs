namespace Phantom.Workspaces.Tools;

public sealed record WorkspaceToolExecutionResult
{
    public static WorkspaceToolExecutionResult Success() => new();

    public static WorkspaceToolExecutionResult Failure(string errorMessage) =>
        new() { IsSuccess = false, ErrorMessage = errorMessage };

    public bool IsSuccess { get; init; } = true;

    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Optional human-readable content to persist on the <c>tool-execution-result</c> entity (e.g. a
    /// diagnostic summary). When set it takes precedence over <see cref="ErrorMessage"/> as the
    /// persisted result content.
    /// </summary>
    public string? ResultContent { get; init; }
}
