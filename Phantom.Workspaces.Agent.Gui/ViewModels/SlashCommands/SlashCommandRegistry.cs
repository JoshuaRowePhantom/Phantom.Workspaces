using System.Collections.Generic;
using AgentSchema;

namespace Phantom.Workspaces.Agent.Gui.ViewModels.SlashCommands;

/// <summary>
/// Default slash command registry. Always includes <c>/help</c>; includes
/// <c>/working-directory</c> only when the agent definition uses the
/// <c>github-copilot</c> provider.
/// </summary>
public sealed class SlashCommandRegistry : ISlashCommandRegistry
{
    public static readonly SlashCommandRegistry Default = new();

    public IReadOnlyList<ISlashCommandHandler> GetCommands(AgentDefinition? agentDefinition)
    {
        var commands = new List<ISlashCommandHandler>();

        if (IsCopilotAgent(agentDefinition))
        {
            commands.Add(new WorkingDirectorySlashCommandHandler());
        }

        commands.Add(new HelpSlashCommandHandler(commands.AsReadOnly()));

        return commands.AsReadOnly();
    }

    private static bool IsCopilotAgent(AgentDefinition? agentDefinition)
    {
        return agentDefinition is PromptAgent promptAgent
            && string.Equals(
                promptAgent.Model?.Provider,
                "github-copilot",
                StringComparison.OrdinalIgnoreCase);
    }
}
