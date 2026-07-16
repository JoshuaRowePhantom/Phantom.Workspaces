using System.Collections.Generic;

namespace Phantom.Workspaces.Llm.SlashCommands;

/// <summary>
/// Registry of slash commands for an <see cref="AgentChat"/> instance.
/// Supports registration of individual handlers and nested sub-registries.
/// </summary>
public interface ISlashCommandRegistry
{
    /// <summary>Registers <paramref name="handler"/> with this registry.</summary>
    void Register(ISlashCommandHandler handler);

    /// <summary>Registers a sub-registry whose commands are included in <see cref="Commands"/>.</summary>
    void Register(ISlashCommandRegistry registry);

    /// <summary>
    /// The slash command handlers registered for this chat session, including handlers
    /// from any registered sub-registries.
    /// </summary>
    IEnumerable<ISlashCommandHandler> Commands { get; }
}
