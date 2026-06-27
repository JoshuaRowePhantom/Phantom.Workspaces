using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Agent.Gui.ViewModels.SlashCommands;

/// <summary>
/// Handles <c>/help [command]</c>.
/// With no argument: lists all available slash commands.
/// With a command name: shows detailed help for that command.
/// Available for all agent types.
/// </summary>
public sealed class HelpSlashCommandHandler : ISlashCommandHandler
{
    private readonly IReadOnlyList<ISlashCommandHandler> availableCommands;

    public HelpSlashCommandHandler(IReadOnlyList<ISlashCommandHandler> availableCommands)
    {
        ArgumentNullException.ThrowIfNull(availableCommands);
        this.availableCommands = availableCommands;
    }

    public string Name => "help";

    public string Description => "List available commands or show help for a specific command";

    public string Usage => "/help [command]";

    public string? LongDescription => null;

    public Task<SlashCommandResult> ExecuteAsync(
        SlashCommandContext context,
        string arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(arguments))
        {
            return Task.FromResult(ListAllCommands());
        }

        return Task.FromResult(ShowCommandHelp(arguments.Trim()));
    }

    private SlashCommandResult ListAllCommands()
    {
        var lines = this.availableCommands
            .OrderBy(static c => c.Name, StringComparer.Ordinal)
            .Select(static c => $"/{c.Name,-24} {c.Description}");
        return new SlashCommandResult { StatusMessage = string.Join('\n', lines) };
    }

    private SlashCommandResult ShowCommandHelp(string commandName)
    {
        var handler = this.availableCommands.FirstOrDefault(
            c => string.Equals(c.Name, commandName, StringComparison.OrdinalIgnoreCase));

        if (handler is null)
        {
            return new SlashCommandResult { StatusMessage = $"Unknown command: /{commandName}" };
        }

        var body = handler.LongDescription ?? handler.Description;
        var usage = handler.Usage is not null ? $"Usage: {handler.Usage}\n\n" : string.Empty;
        return new SlashCommandResult { StatusMessage = $"{usage}{body}" };
    }
}
