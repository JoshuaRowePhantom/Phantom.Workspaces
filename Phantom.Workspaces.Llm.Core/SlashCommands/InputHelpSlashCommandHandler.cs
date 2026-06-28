using System;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Llm.SlashCommands;

/// <summary>
/// Handles <c>/input-help [on|off]</c>.
/// Toggles (or explicitly sets) the visibility of the keyboard-shortcut help text
/// below the chat input box. The new state is persisted in the user/computer profile.
/// </summary>
public sealed class InputHelpSlashCommandHandler : ISlashCommandHandler
{
    private readonly Func<bool> getValue;
    private readonly Action<bool> setValue;

    public InputHelpSlashCommandHandler(Func<bool> getValue, Action<bool> setValue)
    {
        ArgumentNullException.ThrowIfNull(getValue);
        ArgumentNullException.ThrowIfNull(setValue);
        this.getValue = getValue;
        this.setValue = setValue;
    }

    public string Name => "input-help";

    public string Description => "Show or hide keyboard-shortcut help text below the chat input box";

    public string Usage => "/input-help [on|off]";

    public string? LongDescription => """
        /input-help        — toggle visibility
        /input-help on     — show help text
        /input-help off    — hide help text

        The setting is persisted in the user profile.
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
        return Task.FromResult(new SlashCommandResult { StatusMessage = $"Chat input help text is now {state}." });
    }
}
