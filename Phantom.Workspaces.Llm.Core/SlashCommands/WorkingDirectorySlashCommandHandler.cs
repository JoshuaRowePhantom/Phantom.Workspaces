using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Llm.SlashCommands;

/// <summary>
/// Handles <c>/working-directory [path]</c>.
/// With no argument: reports the current working directory.
/// With a path argument: validates and updates the agent session's working directory.
/// The change takes effect on the next turn via <c>CopilotSdkChatClient.EnsureSessionAsync</c>
/// which detects the signature change and calls <c>ResumeSessionAsync</c> with the new
/// working directory — no agent recreation is required.
/// </summary>
public sealed class WorkingDirectorySlashCommandHandler : ISlashCommandHandler
{
    public string Name => "working-directory";

    public string Description => "Get or set the working directory for this Copilot session";

    public string Usage => "/working-directory [path]";

    public string? LongDescription => """
        /working-directory           — prints the current working directory
        /working-directory <path>    — sets the working directory

        The working directory is forwarded to the Copilot session via ResumeSessionAsync
        on the next turn. The process-level cwd (CopilotClientOptions.Cwd) is fixed at
        client creation time and is unaffected; only the session-level cwd changes.
        """;

    public Task<IReadOnlyList<SlashCommandCompletion>> GetCompletionsAsync(
        SlashCommandContext context,
        string partialArguments,
        CancellationToken cancellationToken)
        => Task.FromResult(DirectoryBrowserCompletionHelper.GetCompletions(partialArguments.Trim()));

    public Task<SlashCommandResult> ExecuteAsync(
        SlashCommandContext context,
        string arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(arguments))
        {
            return Task.FromResult(ReadCurrentWorkingDirectory(context));
        }

        return SetWorkingDirectoryAsync(context, arguments.Trim(), cancellationToken);
    }

    private static SlashCommandResult ReadCurrentWorkingDirectory(SlashCommandContext context)
    {
        var current = context.CurrentParameterValues?.GetValueOrDefault("working-directory");
        var message = string.IsNullOrWhiteSpace(current)
            ? "Working directory: (not set — inherits from host process)"
            : $"Working directory: {current}";
        return new SlashCommandResult { StatusMessage = message };
    }

    private static async Task<SlashCommandResult> SetWorkingDirectoryAsync(
        SlashCommandContext context,
        string path,
        CancellationToken cancellationToken)
    {
        if (context.UpdateParameterValuesAsync is null)
        {
            return new SlashCommandResult
            {
                StatusMessage = "Cannot update working directory: session entity is not persisted.",
            };
        }

        if (!Directory.Exists(path))
        {
            return new SlashCommandResult
            {
                StatusMessage = $"Cannot set working directory: path does not exist or is not a directory: {path}",
            };
        }

        var updated = new Dictionary<string, string>(
            context.CurrentParameterValues ?? new Dictionary<string, string>(),
            StringComparer.Ordinal)
        {
            ["working-directory"] = path,
        };

        await context.UpdateParameterValuesAsync(updated, cancellationToken).ConfigureAwait(false);

        return new SlashCommandResult
        {
            StatusMessage = $"Working directory updated to: {path}",
        };
    }
}
