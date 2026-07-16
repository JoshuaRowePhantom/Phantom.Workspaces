using System.Collections.Generic;

namespace Phantom.Workspaces.Llm.SlashCommands;

/// <summary>
/// A registry that delegates all operations to a swappable <see cref="Current"/> registry.
/// <see cref="AgentChat"/> uses this so that agent (re)creation replaces the inner registry
/// without accumulating stale handlers from previous agent instances.
/// </summary>
public sealed class ReplaceableSlashCommandHandlerRegistry : ISlashCommandRegistry
{
    /// <summary>
    /// The active inner registry. Replacing this immediately changes which handlers
    /// are visible via <see cref="Commands"/>.
    /// </summary>
    public ISlashCommandRegistry Current { get; set; } = new SlashCommandRegistry();

    /// <inheritdoc/>
    public void Register(ISlashCommandHandler handler) => this.Current.Register(handler);

    /// <inheritdoc/>
    public void Register(ISlashCommandRegistry registry) => this.Current.Register(registry);

    /// <inheritdoc/>
    public IEnumerable<ISlashCommandHandler> Commands => this.Current.Commands;
}
