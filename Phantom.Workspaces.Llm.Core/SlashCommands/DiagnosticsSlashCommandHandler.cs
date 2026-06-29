using System;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Llm.SlashCommands;

/// <summary>
/// Handles <c>/diagnostics [on|off]</c>.
/// Toggles (or explicitly sets) the visibility of diagnostic chat messages in the output.
/// This is a pure GUI rendering option registered by the host application.
/// </summary>
public sealed class DiagnosticsSlashCommandHandler : ISlashCommandHandler
{
    private readonly Func<bool> getValue;
    private readonly Action<bool> setValue;

    public DiagnosticsSlashCommandHandler(Func<bool> getValue, Action<bool> setValue)
    {
        ArgumentNullException.ThrowIfNull(getValue);
        ArgumentNullException.ThrowIfNull(setValue);
        this.getValue = getValue;
        this.setValue = setValue;
    }

    public string Name => "diagnostics";

    public string Description => "Show or hide diagnostic messages in the chat output";

    public string Usage => "/diagnostics [on|off]";

    public string? LongDescription => """
        /diagnostics        — toggle visibility
        /diagnostics on     — show diagnostic messages
        /diagnostics off    — hide diagnostic messages
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
        return Task.FromResult(new SlashCommandResult { StatusMessage = $"Diagnostic messages are now {state}." });
    }
}
