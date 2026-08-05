using System.Collections.Generic;
using System.Threading.Tasks;

namespace Phantom.Workspaces.ViewModels;

public sealed class EntityShortcutViewModel : ViewModelBase
{
    private bool isEnabled = true;

    public required Shortcut Shortcut { get; init; }

    public required SubscribedEntityViewModel Entity { get; init; }

    public required ShortcutManager ShortcutManager { get; init; }

    public bool IsEnabled
    {
        get => this.isEnabled;
        set => this.SetProperty(ref this.isEnabled, value);
    }

    public Task<bool> HandleAsync(
        MainWindowViewModel mainWindowViewModel)
    {
        return this.ShortcutManager.HandleShortcutAsync(mainWindowViewModel, this.Shortcut, this.Entity);
    }

    /// <summary>
    /// Builds a fresh, deduped list of shortcuts applicable to <paramref name="entity"/> and
    /// returns it. Fix #1144 — no in-place mutation of a shared collection: each invocation
    /// yields its own list, so overlapping concurrent invocations cannot corrupt each other.
    /// </summary>
    public static async Task<IReadOnlyList<EntityShortcutViewModel>> PopulateShortcutsAsync(
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel entity,
        ShortcutManager shortcutManager)
    {
        var result = new List<EntityShortcutViewModel>();
        await foreach (var shortcut in shortcutManager.GetShortcutsForAsync(mainWindowViewModel, entity))
        {
            result.Add(
                new EntityShortcutViewModel
                {
                    Shortcut = shortcut,
                    Entity = entity,
                    ShortcutManager = shortcutManager,
                });
        }

        return result;
    }
}
