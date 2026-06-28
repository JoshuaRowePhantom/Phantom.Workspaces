using System.Collections.Generic;
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

    /// <summary>
    /// Returns completion candidates for the current partial arguments.
    /// Default returns no completions.
    /// </summary>
    Task<IReadOnlyList<SlashCommandCompletion>> GetCompletionsAsync(
        SlashCommandContext context,
        string partialArguments,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<SlashCommandCompletion>>(Array.Empty<SlashCommandCompletion>());

    /// <summary>
    /// Returns rich help text for this command given the current context and partial arguments.
    /// Default returns <see cref="LongDescription"/> ?? <see cref="Description"/>.
    /// Override to provide dynamic, context-aware help (e.g. showing currently valid values).
    /// </summary>
    Task<string> GetHelpAsync(
        SlashCommandContext context,
        string partialArguments,
        CancellationToken cancellationToken)
        => Task.FromResult(LongDescription ?? Description);
}
