using System.Collections.Generic;
using System.Linq;

namespace Phantom.Workspaces.Llm.SlashCommands;

/// <summary>
/// Mutable slash command registry owned by an <see cref="AgentChat"/> instance.
/// Commands are registered at chat initialisation time by the owning assembly.
/// </summary>
public sealed class SlashCommandRegistry : ISlashCommandRegistry
{
    private readonly List<ISlashCommandHandler> handlers = [];
    private readonly List<ISlashCommandRegistry> subRegistries = [];

    /// <inheritdoc/>
    public IEnumerable<ISlashCommandHandler> Commands =>
        this.handlers.Concat(this.subRegistries.SelectMany(r => r.Commands));

    /// <summary>Registers <paramref name="handler"/> with this registry.</summary>
    public void Register(ISlashCommandHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        this.handlers.Add(handler);
    }

    /// <summary>Registers a sub-registry whose commands are included in <see cref="Commands"/>.</summary>
    public void Register(ISlashCommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        this.subRegistries.Add(registry);
    }
}
