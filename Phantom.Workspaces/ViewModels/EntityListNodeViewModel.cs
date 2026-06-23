using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// A node in the hierarchical entity tree. Owns hierarchy concerns only (its child nodes, expansion
/// state, and the expand affordance). The entity's card content (fields, edit mode, badges, shortcuts)
/// is owned by the node's <see cref="Card"/> (an <see cref="EntityCardViewModel"/>).
/// </summary>
public sealed class EntityListNodeViewModel : ViewModelBase
{
    private bool isExpanded;
    private Action<EntityListNodeViewModel, bool>? onExpansionChanged;

    public EntityListNodeViewModel(
        SubscribedEntityViewModel entity,
        IReadOnlyList<string> nameComponents,
        string sortKey,
        IReadOnlyCollection<EntityFieldEditorViewModel>? fieldEditors = null,
        string? cardViewName = null,
        IEntitySchemaComposer? schemaComposer = null,
        FieldEditorFactory? fieldEditorFactory = null)
    {
        this.Card = new EntityCardViewModel(entity, fieldEditors, cardViewName, schemaComposer, fieldEditorFactory);
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
        IReadOnlyCollection<EntityFieldEditorViewModel>? fieldEditors = null,
        bool isExpanded = false,
        string? cardViewName = null)
    {
        this.Card = new EntityCardViewModel(displayName, entityType, fieldEditors, cardViewName);
        this.NameComponents = nameComponents;
        this.SortKey = sortKey;
        this.ToggleExpandCommand = new RelayCommand(
            _ => this.IsExpanded = !this.IsExpanded,
            _ => this.HasChildren);
        this.isExpanded = isExpanded;
    }

    public EntityCardViewModel Card { get; }

    public SubscribedEntityViewModel? Entity => this.Card.Entity;

    public string DisplayName => this.Card.DisplayName;

    public string EntityType => this.Card.EntityType;

    public IReadOnlyList<string> NameComponents { get; }

    public string SortKey { get; }

    public ObservableCollection<EntityListNodeViewModel> Children { get; } = new();

    public ObservableCollection<EntityListNodeViewModel> VisibleChildren { get; } = new();

    public RelayCommand ToggleExpandCommand { get; }

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

            // Notify parent that expansion state changed so it can manage subscriptions
            this.onExpansionChanged?.Invoke(this, value);
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

    public void SetExpansionChangedCallback(
        Action<EntityListNodeViewModel, bool> callback)
    {
        this.onExpansionChanged = callback;
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
