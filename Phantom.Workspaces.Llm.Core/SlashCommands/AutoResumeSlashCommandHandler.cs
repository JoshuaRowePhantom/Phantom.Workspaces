using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Llm.SlashCommands;

/// <summary>
/// Handles <c>/auto-resume [prompt]</c>.
/// With no argument: toggles auto-resume on or off for the current session.
/// With a prompt argument: enables auto-resume with a custom restart prompt.
/// </summary>
public sealed class AutoResumeSlashCommandHandler : ISlashCommandHandler
{
    /// <summary>
    /// The default prompt sent to the agent when it is resumed without a custom message.
    /// </summary>
    public const string DefaultResumePrompt =
        "You were interrupted and restarted. Continue where you left off.";

    public string Name => "auto-resume";

    public string Description => "Toggle automatic restart of this agent session when the executor restarts";

    public string Usage => "/auto-resume [prompt]";

    public string? LongDescription => """
        /auto-resume               — toggles auto-resume on or off
        /auto-resume <prompt>      — enables auto-resume with a custom restart prompt

        When auto-resume is enabled, the executor will automatically restart this agent
        session on startup and send the configured prompt as the opening message.
        The default prompt is: "You were interrupted and restarted. Continue where you left off."
        """;

    public Task<IReadOnlyList<SlashCommandCompletion>> GetCompletionsAsync(
        SlashCommandContext context,
        string partialArguments,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<SlashCommandCompletion>>(Array.Empty<SlashCommandCompletion>());

    public Task<SlashCommandResult> ExecuteAsync(
        SlashCommandContext context,
        string arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.UpdateAutoResumeAsync is null)
        {
            return Task.FromResult(new SlashCommandResult
            {
                StatusMessage = "Cannot update auto-resume: session entity is not persisted.",
            });
        }

        if (context.TrustedExecutorIdentifier is null)
        {
            return Task.FromResult(new SlashCommandResult
            {
                StatusMessage = "Cannot update auto-resume: executor context is not available.",
            });
        }

        var customPrompt = arguments.Trim();

        if (!string.IsNullOrEmpty(customPrompt))
        {
            return EnableWithCustomPromptAsync(context, customPrompt, cancellationToken);
        }

        return ToggleAsync(context, cancellationToken);
    }

    private static async Task<SlashCommandResult> EnableWithCustomPromptAsync(
        SlashCommandContext context,
        string customPrompt,
        CancellationToken cancellationToken)
    {
        var settings = new AutoResumeSettings
        {
            TrustedExecutor = context.TrustedExecutorIdentifier!,
            ResumePrompt = customPrompt,
        };

        await context.UpdateAutoResumeAsync!(settings, cancellationToken).ConfigureAwait(false);

        return new SlashCommandResult
        {
            StatusMessage = $"Auto-resume enabled. Resume prompt: \"{customPrompt}\"",
        };
    }

    private static async Task<SlashCommandResult> ToggleAsync(
        SlashCommandContext context,
        CancellationToken cancellationToken)
    {
        if (context.CurrentAutoResume is not null)
        {
            await context.UpdateAutoResumeAsync!(null, cancellationToken).ConfigureAwait(false);
            return new SlashCommandResult { StatusMessage = "Auto-resume disabled." };
        }

        var settings = new AutoResumeSettings
        {
            TrustedExecutor = context.TrustedExecutorIdentifier!,
        };

        await context.UpdateAutoResumeAsync!(settings, cancellationToken).ConfigureAwait(false);

        return new SlashCommandResult
        {
            StatusMessage = $"Auto-resume enabled. Default resume prompt: \"{DefaultResumePrompt}\"",
        };
    }
}
