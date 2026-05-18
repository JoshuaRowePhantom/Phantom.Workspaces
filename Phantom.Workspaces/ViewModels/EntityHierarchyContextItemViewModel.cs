using Avalonia;

namespace Phantom.Workspaces.ViewModels;

public sealed class EntityHierarchyContextItemViewModel : ViewModelBase
{
    public EntityHierarchyContextItemViewModel(
        string displayName,
        string entityType,
        int level)
    {
        this.DisplayName = displayName;
        this.EntityType = entityType;
        this.Level = level;
    }

    public string DisplayName { get; }

    public string EntityType { get; }

    public int Level { get; }

    public Thickness IndentMargin => new(this.Level * 22, 0, 0, 0);
}
