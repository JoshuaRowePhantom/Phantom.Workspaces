using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ViewModels;

public sealed class EntityListNodeViewModel : ViewModelBase
{
    private bool isExpanded;
    private readonly SubscribedEntityViewModel? entity;
    private readonly string displayName;
    private readonly string entityType;

    public EntityListNodeViewModel(
        SubscribedEntityViewModel entity,
        IReadOnlyList<string> nameComponents,
        string sortKey)
    {
        this.entity = entity;
        this.displayName = entity.DisplayName;
        this.entityType = entity.EntityType;
        this.NameComponents = nameComponents;
        this.SortKey = sortKey;
        this.ToggleExpandCommand = new RelayCommand(
            _ => this.IsExpanded = !this.IsExpanded,
            _ => this.HasChildren);
    }

    public EntityListNodeViewModel(
        string displayName,
        string entityType,
        IReadOnlyList<string> nameComponents,
        string sortKey,
        bool isExpanded = false)
    {
        this.entity = null;
        this.displayName = displayName;
        this.entityType = entityType;
        this.NameComponents = nameComponents;
        this.SortKey = sortKey;
        this.ToggleExpandCommand = new RelayCommand(
            _ => this.IsExpanded = !this.IsExpanded,
            _ => this.HasChildren);
        this.isExpanded = isExpanded;
    }

    public SubscribedEntityViewModel? Entity => this.entity;

    public IReadOnlyList<string> NameComponents { get; }

    public string SortKey { get; }

    public ObservableCollection<EntityListNodeViewModel> Children { get; } = new();

    public ObservableCollection<EntityListNodeViewModel> VisibleChildren { get; } = new();

    public RelayCommand ToggleExpandCommand { get; }

    public string DisplayName => this.entity?.DisplayName ?? this.displayName;

    public string EntityType => this.entity?.EntityType ?? this.entityType;

    public IReadOnlyCollection<string> DisplayItems => this.entity?.DisplayItems ?? Array.Empty<string>();

    public bool HasChildren => this.Children.Count > 0;

    public bool IsExpanded
    {
        get => this.isExpanded;
        set
        {
            if (!this.SetProperty(ref this.isExpanded, value))
            {
                return;
            }

            this.VisibleChildren.Clear();
            if (value)
            {
                foreach (var child in this.Children)
                {
                    this.VisibleChildren.Add(child);
                }
            }

            this.RaisePropertyChanged(nameof(this.ExpandArrow));
        }
    }

    public string ExpandArrow => this.IsExpanded ? "▴" : "▾";

    public CornerRadius ContentCornerRadius => this.HasChildren
        ? new CornerRadius(6, 6, 0, 0)
        : new CornerRadius(6);

    public CornerRadius ExpandSectionCornerRadius => new CornerRadius(0, 0, 6, 6);

    public void SetChildren(
        IReadOnlyCollection<EntityListNodeViewModel> children)
    {
        this.Children.Clear();
        foreach (var child in children)
        {
            this.Children.Add(child);
        }

        this.ToggleExpandCommand.RaiseCanExecuteChanged();
        this.RaisePropertyChanged(nameof(this.HasChildren));
        this.RaisePropertyChanged(nameof(this.ExpandArrow));
        this.RaisePropertyChanged(nameof(this.ContentCornerRadius));
        this.RaisePropertyChanged(nameof(this.ExpandSectionCornerRadius));

        if (!this.IsExpanded)
        {
            this.VisibleChildren.Clear();
            return;
        }

        this.VisibleChildren.Clear();
        foreach (var child in this.Children)
        {
            this.VisibleChildren.Add(child);
        }
    }

    public static bool TryGetPrimaryName(
        JsonElement entityData,
        out EntityName entityName)
    {
        entityName = default;
        if (!entityData.TryGetProperty("names", out var names)
            || names.ValueKind != JsonValueKind.Array
            || names.GetArrayLength() == 0)
        {
            return false;
        }

        var firstName = names[0];
        var parsedName = firstName.TryReadEntityName();
        if (parsedName is null)
        {
            return false;
        }

        entityName = parsedName.Value;
        return true;
    }
}
