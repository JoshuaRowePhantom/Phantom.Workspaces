using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot.SDK;

namespace Phantom.Workspaces.Llm.SlashCommands;

/// <summary>
/// Handles <c>/model [model-id]</c>.
/// With no argument: reports the current model.
/// With a model id: switches the active model for this chat session.
/// Self-registered by <see cref="CopilotSdkChatClient"/> when a slash command registry is provided.
/// </summary>
internal sealed class CopilotSdkModelSlashCommandHandler : ISlashCommandHandler
{
    private readonly IModelSlashCommandClient client;

    public CopilotSdkModelSlashCommandHandler(IModelSlashCommandClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        this.client = client;
    }

    public string Name => "model";

    public string Description => "List available models or set the active model for this chat session";

    public string? Usage => "/model [model-id]";

    public string? LongDescription => null;

    public async Task<IReadOnlyList<SlashCommandCompletion>> GetCompletionsAsync(
        SlashCommandContext context,
        string partialArguments,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ModelInfo> models;
        try
        {
            models = await this.client.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return Array.Empty<SlashCommandCompletion>();
        }

        return models
            .Where(m => m.Id.StartsWith(partialArguments.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(m => new SlashCommandCompletion(
                CompletionText: m.Id,
                Label: m.Id,
                Description: FormatDescription(m)))
            .ToArray();
    }

    public Task<SlashCommandResult> ExecuteAsync(
        SlashCommandContext context,
        string arguments,
        CancellationToken cancellationToken)
    {
        var modelId = arguments.Trim();
        if (string.IsNullOrEmpty(modelId))
        {
            return Task.FromResult(new SlashCommandResult { StatusMessage = $"Active model: {this.client.ModelId}" });
        }

        this.client.SetModelId(modelId);
        return Task.FromResult(new SlashCommandResult { StatusMessage = $"Model set to: {modelId}" });
    }

    private static string FormatDescription(ModelInfo model)
    {
        var parts = new List<string> { model.Name };

        // Per-token prices require a newer GitHub.Copilot.SDK version (issue #899).
        if (model.Billing?.Multiplier is { } multiplier && multiplier != 1.0)
        {
            parts.Add($"x{multiplier:F2} billing");
        }

        return string.Join(" · ", parts);
    }
}
