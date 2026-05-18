using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Text.Json;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ViewModels;

public sealed class EntityBrowserTreeNodeViewModel : ViewModelBase
{
    private bool isExpanded;

    public EntityBrowserTreeNodeViewModel(
        SubscribedEntityViewModel entity,
        IReadOnlyList<string> nameComponents,
        string sortKey)
    {
        this.Entity = entity;
        this.NameComponents = nameComponents;
        this.SortKey = sortKey;
        this.ToggleExpandCommand = new RelayCommand(
            _ => this.IsExpanded = !this.IsExpanded,
            _ => this.HasChildren);
    }

    public SubscribedEntityViewModel Entity { get; }

    public IReadOnlyList<string> NameComponents { get; }

    public string SortKey { get; }

    public ObservableCollection<EntityBrowserTreeNodeViewModel> Children { get; } = new();

    public ObservableCollection<EntityBrowserTreeNodeViewModel> VisibleChildren { get; } = new();

    public RelayCommand ToggleExpandCommand { get; }

    public string DisplayName => this.Entity.DisplayName;

    public string EntityType => this.Entity.EntityType;

    public IReadOnlyCollection<string> DisplayItems => this.Entity.DisplayItems;

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

    public string ExpandArrow => this.IsExpanded ? "▲" : "▼";

    public void SetChildren(
        IReadOnlyCollection<EntityBrowserTreeNodeViewModel> children)
    {
        this.Children.Clear();
        foreach (var child in children)
        {
            this.Children.Add(child);
        }

        this.ToggleExpandCommand.RaiseCanExecuteChanged();
        this.RaisePropertyChanged(nameof(this.HasChildren));
        this.RaisePropertyChanged(nameof(this.ExpandArrow));

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
        if (firstName.ValueKind == JsonValueKind.String)
        {
            var text = firstName.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            entityName = new EntityName(text);
            return true;
        }

        var parsedName = firstName.TryReadEntityName();
        if (parsedName is null)
        {
            return false;
        }

        entityName = parsedName.Value;
        return true;
    }
}
