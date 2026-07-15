using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Llm.SlashCommands;

/// <summary>
/// Handles <c>/help [command]</c>.
/// With no argument: lists all available slash commands.
/// With a command name: shows detailed help for that command.
/// Available for all agent types.
/// </summary>
public sealed class HelpSlashCommandHandler : ISlashCommandHandler
{
    private readonly ISlashCommandRegistry registry;

    public HelpSlashCommandHandler(ISlashCommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        this.registry = registry;
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

        return ShowCommandHelpAsync(context, arguments.Trim(), cancellationToken);
    }

    public async Task<IReadOnlyList<SlashCommandCompletion>> GetCompletionsAsync(
        SlashCommandContext context,
        string partialArguments,
        CancellationToken cancellationToken)
    {
        var trimmed = partialArguments.TrimStart();
        var firstWord = trimmed.Split(' ', 2)[0];
        var exactMatch = this.registry.Commands.FirstOrDefault(
            c => string.Equals(c.Name, firstWord, StringComparison.OrdinalIgnoreCase));

        if (exactMatch is not null && trimmed.Contains(' '))
        {
            var subArgs = trimmed.Substring(firstWord.Length).TrimStart();
            return await exactMatch.GetCompletionsAsync(context, subArgs, cancellationToken);
        }

        return this.registry.Commands
            .Where(c => c.Name.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => new SlashCommandCompletion(c.Name, $"/{c.Name}", c.Description))
            .ToArray();
    }

    private SlashCommandResult ListAllCommands()
    {
        var lines = this.registry.Commands
            .OrderBy(static c => c.Name, StringComparer.Ordinal)
            .Select(static c => $"/{c.Name,-24} {c.Description}");
        return new SlashCommandResult 
        { 
            StatusMessage = string.Join('\n', lines),
            Role = AgentChatHistoryItem.HelpChatRole
        };
    }

    private async Task<SlashCommandResult> ShowCommandHelpAsync(
        SlashCommandContext context,
        string commandName,
        CancellationToken cancellationToken)
    {
        var handler = this.registry.Commands.FirstOrDefault(
            c => string.Equals(c.Name, commandName, StringComparison.OrdinalIgnoreCase));

        if (handler is null)
        {
            return new SlashCommandResult { StatusMessage = $"Unknown command: /{commandName}" };
        }

        var body = await handler.GetHelpAsync(context, string.Empty, cancellationToken);
        var usage = handler.Usage is not null ? $"Usage: {handler.Usage}\n\n" : string.Empty;
        return new SlashCommandResult 
        { 
            StatusMessage = $"{usage}{body}",
            Role = AgentChatHistoryItem.HelpChatRole
        };
    }
}
