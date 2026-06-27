using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Llm.SlashCommands;

/// <summary>
/// Handles a single slash command entered in the chat input.
/// Commands are intercepted before the message reaches the underlying LLM.
/// </summary>
public interface ISlashCommandHandler
{
    /// <summary>Command name without the leading slash, e.g. "working-directory".</summary>
    string Name { get; }

    /// <summary>Short description shown in the command picker and by /help.</summary>
    string Description { get; }

    /// <summary>One-line usage string, e.g. "/working-directory [path]". Optional.</summary>
    string? Usage { get; }

    /// <summary>Extended help text shown by /help. Falls back to Description when null.</summary>
    string? LongDescription { get; }

    /// <summary>
    /// Executes the command. Receives the remainder of the input after the command name (trimmed),
    /// or an empty string when no argument was supplied.
    /// </summary>
    Task<SlashCommandResult> ExecuteAsync(
        SlashCommandContext context,
        string arguments,
        CancellationToken cancellationToken);
}
