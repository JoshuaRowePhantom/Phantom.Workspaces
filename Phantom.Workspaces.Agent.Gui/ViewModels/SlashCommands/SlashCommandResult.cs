namespace Phantom.Workspaces.Agent.Gui.ViewModels.SlashCommands;

/// <summary>
/// The outcome of executing a slash command.
/// </summary>
public sealed record SlashCommandResult
{
    /// <summary>Status message shown inline to the user (not added to conversation history).</summary>
    public required string StatusMessage { get; init; }

    /// <summary>
    /// When <see langword="true"/>, the chat UI must dispose the current <c>AgentChat</c>
    /// and reconstruct it from the updated agent-session entity so new configuration
    /// (such as a changed working directory) takes effect.
    /// </summary>
    public bool RequiresAgentRecreation { get; init; }
}
