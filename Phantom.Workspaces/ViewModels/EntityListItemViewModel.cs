using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;

namespace Phantom.Workspaces.ViewModels;

public sealed class EntityListItemViewModel : ViewModelBase
{
    private bool isExpanded;

    public EntityListItemViewModel(
        EntityListNodeViewModel node,
        int order,
        int level,
        string itemKey,
        string? parentItemKey = null,
        IReadOnlyCollection<string>? childItemKeys = null,
        bool isExpanded = false)
    {
        this.Node = node;
        this.Order = order;
        this.Level = level;
        this.ItemKey = itemKey;
        this.ParentItemKey = parentItemKey;
        this.ChildItemKeys = childItemKeys?.ToArray() ?? Array.Empty<string>();
        this.isExpanded = isExpanded;
        this.ToggleExpandCommand = new RelayCommand(
            _ => this.IsExpanded = !this.IsExpanded,
            _ => this.HasChildren);
    }

    public EntityListNodeViewModel Node { get; }

    public int Order { get; }

    public int Level { get; }

    public string ItemKey { get; }

    public string? ParentItemKey { get; }

    public IReadOnlyCollection<string> ChildItemKeys { get; }

    public bool HasChildren => this.ChildItemKeys.Count > 0;

    public RelayCommand ToggleExpandCommand { get; }

    public Thickness IndentMargin => new(this.Level * 22, 0, 0, 4);

    public string DisplayName => this.Node.DisplayName;

    public string EntityType => this.Node.EntityType;

    public IReadOnlyCollection<EntityDisplayItemViewModel> DisplayItems => this.Node.DisplayItems;

    public string ExpandArrow => this.IsExpanded ? "▴" : "▾";

    public CornerRadius ContentCornerRadius => this.HasChildren
        ? new CornerRadius(6, 6, 0, 0)
        : new CornerRadius(6);

    public CornerRadius ExpandSectionCornerRadius => new(0, 0, 6, 6);

    public bool IsExpanded
    {
        get => this.isExpanded;
        set
        {
            if (!this.SetProperty(ref this.isExpanded, value))
            {
                return;
            }

            this.Node.IsExpanded = value;
            this.RaisePropertyChanged(nameof(this.ExpandArrow));
        }
    }
}
