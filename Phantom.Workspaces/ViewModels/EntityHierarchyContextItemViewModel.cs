using Avalonia;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Phantom.Workspaces.ViewModels;

public sealed class EntityHierarchyContextItemViewModel : ViewModelBase
{
    public EntityHierarchyContextItemViewModel(
        string displayName,
        string entityType,
        int level,
        IReadOnlyCollection<EntityDisplayItemViewModel>? displayItems = null)
    {
        this.DisplayName = displayName;
        this.EntityType = entityType;
        this.Level = level;
        this.DisplayItems = displayItems?.ToArray() ?? Array.Empty<EntityDisplayItemViewModel>();
    }

    public string DisplayName { get; }

    public string EntityType { get; }

    public int Level { get; }

    public IReadOnlyCollection<EntityDisplayItemViewModel> DisplayItems { get; }

    public Thickness IndentMargin => new(this.Level * 22, 0, 0, 4);
}
