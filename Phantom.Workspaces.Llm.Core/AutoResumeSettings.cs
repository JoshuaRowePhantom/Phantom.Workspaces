namespace Phantom.Workspaces.Llm;

/// <summary>
/// Describes the auto-resume configuration stored on an <c>agent-session</c> entity.
/// When present, the trusted executor will automatically resume the session on startup.
/// </summary>
public sealed record AutoResumeSettings
{
    /// <summary>
    /// Identifier of the trusted executor that should resume this agent on startup.
    /// Uses the same format as the client-instance identifier:
    /// <c>"."</c> for the local executor, or a UUID string for a remote executor.
    /// </summary>
    public required string TrustedExecutor { get; init; }

    /// <summary>
    /// Prompt sent to the agent when it is auto-resumed.
    /// When <see langword="null"/> or empty, the default fallback is used.
    /// </summary>
    public string? ResumePrompt { get; init; }
}
