using System.Collections.Generic;

namespace Phantom.Workspaces.Llm.SlashCommands;

/// <summary>
/// Mutable slash command registry owned by an <see cref="AgentChat"/> instance.
/// Commands are registered at chat initialisation time by the owning assembly.
/// </summary>
public sealed class SlashCommandRegistry : ISlashCommandRegistry
{
    private readonly List<ISlashCommandHandler> commands = [];

    /// <inheritdoc/>
    public IReadOnlyList<ISlashCommandHandler> Commands => this.commands.AsReadOnly();

    /// <summary>Registers <paramref name="handler"/> with this registry.</summary>
    public void Register(ISlashCommandHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        this.commands.Add(handler);
    }
}
