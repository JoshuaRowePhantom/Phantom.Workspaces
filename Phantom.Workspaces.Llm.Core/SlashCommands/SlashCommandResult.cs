using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm.SlashCommands;

/// <summary>
/// The outcome of executing a slash command.
/// </summary>
public sealed record SlashCommandResult
{
    /// <summary>Status message shown inline to the user (not added to conversation history).</summary>
    public required string StatusMessage { get; init; }
    
    /// <summary>Optional role hint for how the status message should be rendered.</summary>
    public ChatRole? Role { get; init; }
}
