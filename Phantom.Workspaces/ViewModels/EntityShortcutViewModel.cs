using System.Collections.ObjectModel;
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

    public static async Task PopulateShortcutsAsync(
        ObservableCollection<EntityShortcutViewModel> shortcuts,
        MainWindowViewModel mainWindowViewModel,
        SubscribedEntityViewModel entity,
        ShortcutManager shortcutManager)
    {
        shortcuts.Clear();
        await foreach (var shortcut in shortcutManager.GetShortcutsForAsync(mainWindowViewModel, entity))
        {
            shortcuts.Add(
                new EntityShortcutViewModel
                {
                    Shortcut = shortcut,
                    Entity = entity,
                    ShortcutManager = shortcutManager,
                });
        }
    }
}
