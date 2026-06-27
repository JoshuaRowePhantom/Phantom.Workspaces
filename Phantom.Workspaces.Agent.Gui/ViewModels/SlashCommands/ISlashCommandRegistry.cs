using System.Collections.Generic;
using AgentSchema;

namespace Phantom.Workspaces.Agent.Gui.ViewModels.SlashCommands;

/// <summary>
/// Provides the set of slash commands applicable to a given agent definition.
/// </summary>
public interface ISlashCommandRegistry
{
    /// <summary>
    /// Returns the slash command handlers registered for the given agent definition.
    /// The <c>/help</c> command is always included; provider-specific commands are
    /// included only when the definition's provider matches.
    /// </summary>
    IReadOnlyList<ISlashCommandHandler> GetCommands(AgentDefinition? agentDefinition);
}
