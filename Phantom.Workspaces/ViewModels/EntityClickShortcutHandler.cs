using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Handles a plain click on an entity card: for configured entity types (for example
/// <c>workspace</c>), it invokes the <see cref="Shortcut.Open"/> shortcut through the shortcut
/// handling pathway so the entity opens, reusing <see cref="OpenEntityShortcutHandler"/>.
/// </summary>
/// <remarks>
/// This handler is deliberately <b>not</b> registered with the <see cref="ShortcutManager"/>: because
/// <see cref="ShortcutManager.GetShortcutsFor"/> only consults registered handlers, leaving it
/// unregistered guarantees it contributes no shortcut button. It is invoked directly from the entity
/// card click wiring instead. See <c>docs/design/entity-click-shortcut-handler.md</c>.
/// </remarks>
public sealed class EntityClickShortcutHandler : ShortcutHandler
{
    private readonly IReadOnlyCollection<string> clickOpenableEntityTypes;
    private readonly ShortcutManager shortcutManager;

    /// <summary>
    /// Creates the handler for the given click-openable entity types, delegating the actual open to
    /// the supplied <paramref name="shortcutManager"/> (which owns the real
    /// <see cref="OpenEntityShortcutHandler"/>).
    /// </summary>
    public EntityClickShortcutHandler(
        IReadOnlyCollection<string> clickOpenableEntityTypes,
        ShortcutManager shortcutManager)
    {
        ArgumentNullException.ThrowIfNull(clickOpenableEntityTypes);
        this.shortcutManager = shortcutManager ?? throw new ArgumentNullException(nameof(shortcutManager));
        this.clickOpenableEntityTypes = clickOpenableEntityTypes;
    }

    /// <inheritdoc />
    public override bool ShouldApplyTo(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        ArgumentNullException.ThrowIfNull(entityViewModel);

        // Invoked directly on click, so the incoming shortcut is ignored; only the entity type
        // determines whether a click opens the entity.
        return this.clickOpenableEntityTypes.Any(entityViewModel.IsEntityType);
    }

    /// <inheritdoc />
    public override async Task<bool> Handle(
        MainWindowViewModel mainWindowViewModel,
        Shortcut shortcut,
        SubscribedEntityViewModel entityViewModel)
    {
        ArgumentNullException.ThrowIfNull(mainWindowViewModel);
        ArgumentNullException.ThrowIfNull(entityViewModel);

        if (!this.ShouldApplyTo(mainWindowViewModel, shortcut, entityViewModel))
        {
            return false;
        }

        return await this.shortcutManager
            .HandleShortcutAsync(mainWindowViewModel, Shortcut.Open, entityViewModel)
            .ConfigureAwait(true);
    }
}
