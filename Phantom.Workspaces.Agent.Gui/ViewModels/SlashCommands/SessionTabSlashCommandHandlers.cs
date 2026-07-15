using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Llm.SlashCommands;

public sealed class RestartSlashCommandHandler : ISlashCommandHandler
{
    public string Name => "restart";
    public string Description => "Clone the current session and replace this tab with the new session";
    public string? Usage => "/restart";
    public string? LongDescription => null;

    public async Task<SlashCommandResult> ExecuteAsync(SlashCommandContext context, string arguments, CancellationToken cancellationToken)
    {
        if (context.ReplaceWithCloneAsync is null)
        {
            return new SlashCommandResult { StatusMessage = "Cannot restart: session cloning is not available." };
        }

        await context.ReplaceWithCloneAsync(cancellationToken).ConfigureAwait(false);
        return new SlashCommandResult { StatusMessage = "Session restarted." };
    }

    public Task<IReadOnlyList<SlashCommandCompletion>> GetCompletionsAsync(SlashCommandContext context, string partialArguments, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<SlashCommandCompletion>>(Array.Empty<SlashCommandCompletion>());
}

public sealed class CloneSlashCommandHandler : ISlashCommandHandler
{
    public string Name => "clone";
    public string Description => "Clone the current session and open it in a new tab";
    public string? Usage => "/clone";
    public string? LongDescription => null;

    public async Task<SlashCommandResult> ExecuteAsync(SlashCommandContext context, string arguments, CancellationToken cancellationToken)
    {
        if (context.OpenCloneInNewTabAsync is null)
        {
            return new SlashCommandResult { StatusMessage = "Cannot clone: session cloning is not available." };
        }

        await context.OpenCloneInNewTabAsync(cancellationToken).ConfigureAwait(false);
        return new SlashCommandResult { StatusMessage = "Session cloned." };
    }

    public Task<IReadOnlyList<SlashCommandCompletion>> GetCompletionsAsync(SlashCommandContext context, string partialArguments, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<SlashCommandCompletion>>(Array.Empty<SlashCommandCompletion>());
}

public sealed class RenameSlashCommandHandler : ISlashCommandHandler
{
    public string Name => "rename";
    public string Description => "Set the display name of this session";
    public string? Usage => "/rename <new name>";
    public string? LongDescription => null;

    public async Task<SlashCommandResult> ExecuteAsync(SlashCommandContext context, string arguments, CancellationToken cancellationToken)
    {
        var name = arguments.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return new SlashCommandResult { StatusMessage = "Usage: /rename <new name>" };
        }

        if (context.RenameSessionAsync is null)
        {
            return new SlashCommandResult { StatusMessage = "Cannot rename: session entity is not available." };
        }

        await context.RenameSessionAsync(name, cancellationToken).ConfigureAwait(false);
        return new SlashCommandResult { StatusMessage = $"Session renamed to \"{name}\"." };
    }

    public Task<IReadOnlyList<SlashCommandCompletion>> GetCompletionsAsync(SlashCommandContext context, string partialArguments, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<SlashCommandCompletion>>(Array.Empty<SlashCommandCompletion>());
}

public sealed class TitleSlashCommandHandler : ISlashCommandHandler
{
    public string Name => "title";
    public string Description => "Set the tab title for this session";
    public string? Usage => "/title <new title>";
    public string? LongDescription => null;

    public Task<SlashCommandResult> ExecuteAsync(SlashCommandContext context, string arguments, CancellationToken cancellationToken)
    {
        var title = arguments.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            return Task.FromResult(new SlashCommandResult { StatusMessage = "Usage: /title <new title>" });
        }

        if (context.SetTabTitleAsync is null)
        {
            return Task.FromResult(new SlashCommandResult { StatusMessage = "Cannot set title: tab context is not available." });
        }

        return ExecuteCoreAsync(context, title, cancellationToken);
    }

    public Task<IReadOnlyList<SlashCommandCompletion>> GetCompletionsAsync(SlashCommandContext context, string partialArguments, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<SlashCommandCompletion>>(Array.Empty<SlashCommandCompletion>());

    private static async Task<SlashCommandResult> ExecuteCoreAsync(SlashCommandContext context, string title, CancellationToken cancellationToken)
    {
        await context.SetTabTitleAsync!(title, cancellationToken).ConfigureAwait(false);
        return new SlashCommandResult { StatusMessage = $"Tab title set to \"{title}\"." };
    }
}
