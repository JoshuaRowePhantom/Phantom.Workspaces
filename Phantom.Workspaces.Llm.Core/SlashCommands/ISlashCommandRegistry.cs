using System.Collections.Generic;

namespace Phantom.Workspaces.Llm.SlashCommands;

/// <summary>
/// Read-only view of the slash commands registered for an <see cref="AgentChat"/> instance.
/// </summary>
public interface ISlashCommandRegistry
{
    /// <summary>
    /// The slash command handlers registered for this chat session.
    /// The <c>/help</c> command is always included; provider-specific commands are
    /// included only for the matching provider.
    /// </summary>
    IReadOnlyList<ISlashCommandHandler> Commands { get; }
}
