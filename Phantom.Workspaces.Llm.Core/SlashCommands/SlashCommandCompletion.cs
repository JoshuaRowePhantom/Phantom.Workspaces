namespace Phantom.Workspaces.Llm.SlashCommands;

/// <summary>A single completion candidate returned by a slash command handler.</summary>
public sealed record SlashCommandCompletion(
    /// <summary>The full text to substitute into the composer when this completion is accepted.</summary>
    string CompletionText,
    /// <summary>Optional label shown in the popup. Falls back to <see cref="CompletionText"/> when null.</summary>
    string? Label = null,
    /// <summary>Optional description shown alongside the label.</summary>
    string? Description = null);
