using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Llm.SlashCommands;

/// <summary>
/// Handles <c>/reasoning [on|off|toggle]</c>.
/// Toggles (or explicitly sets) the visibility of reasoning tokens in the chat output.
/// </summary>
public sealed class ReasoningSlashCommandHandler : ISlashCommandHandler
{
    private readonly Func<bool> getValue;
    private readonly Action<bool> setValue;

    public ReasoningSlashCommandHandler(Func<bool> getValue, Action<bool> setValue)
    {
        ArgumentNullException.ThrowIfNull(getValue);
        ArgumentNullException.ThrowIfNull(setValue);
        this.getValue = getValue;
        this.setValue = setValue;
    }

    public string Name => "reasoning";

    public string Description => "Show or hide reasoning tokens in the chat output";

    public string Usage => "/reasoning [on|off|toggle]";

    public string? LongDescription => """
        /reasoning           — toggle visibility
        /reasoning on        — show reasoning tokens
        /reasoning off       — hide reasoning tokens
        /reasoning toggle    — toggle visibility
        """;

    public Task<SlashCommandResult> ExecuteAsync(
        SlashCommandContext context,
        string arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var newValue = arguments.Trim().ToLowerInvariant() switch
        {
            "on" => true,
            "off" => false,
            _ => !this.getValue(),
        };

        this.setValue(newValue);
        var state = newValue ? "visible" : "hidden";
        return Task.FromResult(new SlashCommandResult { StatusMessage = $"Reasoning tokens are now {state}." });
    }

    public Task<IReadOnlyList<SlashCommandCompletion>> GetCompletionsAsync(
        SlashCommandContext context,
        string partialArguments,
        CancellationToken cancellationToken)
    {
        var completions = new[] { "on", "off", "toggle" }
            .Where(s => s.StartsWith(partialArguments.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(s => new SlashCommandCompletion(s))
            .ToList<SlashCommandCompletion>();
        return Task.FromResult<IReadOnlyList<SlashCommandCompletion>>(completions);
    }
}
