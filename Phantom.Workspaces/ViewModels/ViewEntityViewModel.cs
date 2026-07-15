using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Generic;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;

namespace Phantom.Workspaces.ViewModels;

public sealed class ViewEntityViewModel : ViewModelBase
{
    private readonly ObservableCollection<EntityDisplayItemViewModel> displayItems = [];
    private readonly EntityListNodeViewModel entityCardNode;
    private bool hasTraversedChildren;
    private bool isExpanded = true;
    private IBrush? parentColorBrush;

    public ViewEntityViewModel(
        SubscribedEntityViewModel entity,
        MainWindowViewModel mainWindowViewModel,
        ShortcutManager shortcutManager,
        int indentLevel,
        bool isExpanded = true,
        bool isParentContext = false,
        FieldEditorFactory? fieldEditorFactory = null)
    {
        this.Entity = entity;
        this.Badges = new BadgesViewModel(entity.Badges);
        this.StatusBadges = new StatusBadgesViewModel(entity.StatusBadges);
        this.IndentLevel = indentLevel;
        this.IsExpanded = isExpanded;
        this.IsParentContext = isParentContext;
        this.entityCardNode = new EntityListNodeViewModel(
            entity,
            ResolveNameComponents(entity),
            entity.EntityId.ToString(),
            cardViewName: EntityCardViewResolver.RawViewName,
            fieldEditorFactory: fieldEditorFactory);
        mainWindowViewModel.RegisterCardNode(entity, this.entityCardNode);
        EntityShortcutViewModel.PopulateShortcuts(this.Shortcuts, mainWindowViewModel, entity, shortcutManager);
        this.entityCardNode.Card.SetShortcuts(this.Shortcuts, mainWindowViewModel.ActivateShortcutCommand);
        this.entityCardNode.Card.SetBadges(this.Badges);
        this.entityCardNode.Card.SetStatusBadges(this.StatusBadges);
        this.Entity.PropertyChanged += this.OnEntityPropertyChanged;
        this.ToggleExpandCommand = new RelayCommand(
            execute: _ => this.IsExpanded = !this.IsExpanded,
            canExecute: _ => this.HasTraversedChildren);
        this.RefreshCollections();
    }

    public SubscribedEntityViewModel Entity { get; }

    public string EntityId => this.Entity.EntityId.ToString();

    public string DisplayName => this.Entity.DisplayName;

    public string EntityType => this.Entity.EntityType;

    public int IndentLevel { get; }

    public bool IsParentContext { get; }

    public bool HasTraversedChildren
    {
        get => this.hasTraversedChildren;
        internal set
        {
            if (this.SetProperty(ref this.hasTraversedChildren, value))
            {
                this.ToggleExpandCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsExpanded
    {
        get => this.isExpanded;
        internal set
        {
            if (this.SetProperty(ref this.isExpanded, value))
            {
                this.RaisePropertyChanged(nameof(this.ExpandArrow));
            }
        }
    }

    public string ExpandArrow => this.isExpanded ? "▴" : "▾";

    public RelayCommand ToggleExpandCommand { get; }

    public BadgesViewModel Badges { get; }

    public StatusBadgesViewModel StatusBadges { get; }

    public ObservableCollection<EntityDisplayItemViewModel> DisplayItems => this.displayItems;

    public ObservableCollection<EntityShortcutViewModel> Shortcuts { get; } = [];

    public ObservableCollection<ViewEntityViewModel> Children { get; } = [];

    public EntityListNodeViewModel EntityCardNode => this.entityCardNode;

    public bool HasShortcuts => this.Shortcuts.Count > 0;

    public IBrush? ParentColorBrush
    {
        get => this.parentColorBrush;
        private set
        {
            if (this.SetProperty(ref this.parentColorBrush, value))
            {
                this.RaisePropertyChanged(nameof(this.HasParent));
            }
        }
    }

    public bool HasParent => this.ParentColorBrush is not null;

    public void AddChild(ViewEntityViewModel child)
    {
        child.ParentColorBrush = Converters.EntityTypeColorConverter.Instance.Convert(
            this.Entity.NonAbstractEntityTypeNames,
            typeof(IBrush),
            null,
            System.Globalization.CultureInfo.InvariantCulture) as IBrush;
        this.Children.Add(child);
        this.HasTraversedChildren = true;
    }

    private void OnEntityPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(SubscribedEntityViewModel.Snapshot), System.StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(SubscribedEntityViewModel.DisplayName), System.StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(SubscribedEntityViewModel.EntityType), System.StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.DisplayName));
            this.RaisePropertyChanged(nameof(this.EntityType));
            this.RefreshCollections();
        }
    }

    private void RefreshCollections()
    {
        this.displayItems.Clear();

        if (this.Entity.Snapshot.Data is not JsonElement)
        {
            return;
        }

        foreach (var displayItem in this.Entity.DisplayItems)
        {
            this.displayItems.Add(displayItem);
        }

        this.RaisePropertyChanged(nameof(this.HasShortcuts));
    }

    private static IReadOnlyList<string> ResolveNameComponents(
        SubscribedEntityViewModel entity)
    {
        if (entity.Data is JsonElement entityData
            && EntityListNodeViewModel.TryGetPrimaryName(entityData, out var entityName))
        {
            return entityName.Components;
        }

        return [entity.EntityId.ToString()];
    }
}
