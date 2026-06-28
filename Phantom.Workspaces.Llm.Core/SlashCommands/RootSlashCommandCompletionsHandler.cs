using System;
using System.Collections.Generic;
using System.Linq;

namespace Phantom.Workspaces.Llm.SlashCommands;

/// <summary>
/// Returns completion candidates for partial command-name input (when the user has typed
/// "/" or "/partial-name" with no space yet). Not registered as a named slash command —
/// wired directly into the composer as the fallback provider for the unresolved case.
/// </summary>
public sealed class RootSlashCommandCompletionsHandler
{
    private readonly ISlashCommandRegistry registry;

    public RootSlashCommandCompletionsHandler(ISlashCommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        this.registry = registry;
    }

    /// <summary>
    /// Returns one completion per registered command whose name starts with
    /// <paramref name="partialCommandName"/>, ordered alphabetically.
    /// </summary>
    public IReadOnlyList<SlashCommandCompletion> GetCompletions(string partialCommandName)
        => this.registry.Commands
            .Where(c => c.Name.StartsWith(partialCommandName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => new SlashCommandCompletion(
                CompletionText: $"{c.Name} ",
                Label: $"/{c.Name}",
                Description: c.Description))
            .ToArray();
}
