using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Llm.SlashCommands;

/// <summary>
/// A <see cref="CopilotSdkChatClient"/>-specific wrapper around
/// <see cref="WorkingDirectorySlashCommandHandler"/> that, in addition to persisting the new path
/// (when the session entity is available), also updates the live
/// <see cref="CopilotSdkChatClient"/> in-memory working directory immediately via
/// <see cref="CopilotSdkChatClient.SetWorkingDirectory"/>.  This ensures that the next call to
/// <c>EnsureSessionAsync</c> detects a session-signature change and resumes the Copilot CLI session
/// with the new working directory without waiting for a process restart or a round-trip through
/// persisted parameter values.
/// </summary>
internal sealed class CopilotSdkWorkingDirectorySlashCommandHandler : ISlashCommandHandler
{
    private readonly WorkingDirectorySlashCommandHandler baseHandler = new();
    private readonly CopilotSdkChatClient client;

    public CopilotSdkWorkingDirectorySlashCommandHandler(CopilotSdkChatClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        this.client = client;
    }

    public string Name => this.baseHandler.Name;

    public string Description => this.baseHandler.Description;

    public string Usage => this.baseHandler.Usage;

    public string? LongDescription => this.baseHandler.LongDescription;

    public Task<IReadOnlyList<SlashCommandCompletion>> GetCompletionsAsync(
        SlashCommandContext context,
        string partialArguments,
        CancellationToken cancellationToken)
        => this.baseHandler.GetCompletionsAsync(context, partialArguments, cancellationToken);

    public async Task<SlashCommandResult> ExecuteAsync(
        SlashCommandContext context,
        string arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(arguments))
        {
            return await this.baseHandler.ExecuteAsync(context, arguments, cancellationToken).ConfigureAwait(false);
        }

        // Inject a non-null callback so the base handler performs path validation and produces a
        // success message even when the session entity has no persisted parameter store.
        // Path validation and the success/error status message remain the base handler's
        // responsibility; updating the live client is ours.
        // The real persist callback (if any) is forwarded from inside the placeholder.
        string? validatedPath = null;
        var contextForBase = context with
        {
            UpdateParameterValuesAsync = async (values, token) =>
            {
                validatedPath = values.GetValueOrDefault("working-directory");
                if (context.UpdateParameterValuesAsync is { } persist)
                {
                    await persist(values, token).ConfigureAwait(false);
                }
            },
        };

        var result = await this.baseHandler.ExecuteAsync(contextForBase, arguments, cancellationToken).ConfigureAwait(false);

        if (validatedPath is not null)
        {
            this.client.SetWorkingDirectory(validatedPath);
        }

        return result;
    }
}
