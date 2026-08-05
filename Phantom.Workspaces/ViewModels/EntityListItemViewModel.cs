using System;
using System.Collections.Generic;
using System.ComponentModel;
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
        this.Node.PropertyChanged += this.OnNodePropertyChanged;
        this.Node.Card.PropertyChanged += this.OnCardPropertyChanged;
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

    public EntityCardViewModel Card => this.Node.Card;

    public int Order { get; private set; }

    public int Level { get; private set; }

    public string ItemKey { get; }

    public string? ParentItemKey { get; private set; }

    public IReadOnlyCollection<string> ChildItemKeys { get; private set; }

    public bool HasChildren => this.ChildItemKeys.Count > 0 || this.Node.HasChildren;

    public int? StickyRow => this.HasChildren ? this.Level : null;

    public RelayCommand ToggleExpandCommand { get; }

    public Thickness IndentMargin => new(this.Level * 22, 0, 0, 4);

    public string DisplayName => this.Card.DisplayName;

    public string EntityType => this.Card.EntityType;

    public IReadOnlyCollection<EntityFieldEditorViewModel> FieldEditors => this.Card.FieldEditors;

    public bool IsEditMode => this.Card.IsEditMode;

    public RelayCommand ToggleEditModeCommand => this.Card.ToggleEditModeCommand;

    public RelayCommand SaveEditModeCommand => this.Card.SaveEditModeCommand;

    public RelayCommand DiscardEditModeCommand => this.Card.DiscardEditModeCommand;

    public string EditModeGlyph => this.Card.EditModeGlyph;

    public bool ShowEditIndicator => this.Card.ShowEditIndicator;

    public bool ShowEditActions => this.Card.ShowEditActions;

    public RelayCommand ToggleJsonViewCommand => this.Card.ToggleJsonViewCommand;

    public bool ShowJsonButton => this.Card.ShowJsonButton;

    public bool ShowDeleteButton => this.Card.ShowDeleteButton;

    public RelayCommand DeleteEntityCommand => this.Card.DeleteEntityCommand;

    public IReadOnlyCollection<EntityShortcutViewModel> Shortcuts => this.Card.Shortcuts;

    public bool HasShortcuts => this.Card.HasShortcuts;

    public RelayCommand? ActivateShortcutCommand => this.Card.ActivateShortcutCommand;

    public bool IsDeleted => this.Card.IsDeleted;

    public bool IsInteractive => this.Card.IsInteractive;

    public bool ShowRawJsonEditor => this.Card.ShowRawJsonEditor;

    public bool IsRawJsonReadOnly => this.Card.IsRawJsonReadOnly;

    public string JsonButtonText => this.Card.JsonButtonText;

    public string RawJsonText
    {
        get => this.Card.RawJsonText;
        set => this.Card.RawJsonText = value;
    }

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

    internal void UpdateStructuralData(
        int order,
        int level,
        string? parentItemKey,
        IReadOnlyCollection<string> childItemKeys)
    {
        bool levelChanged = this.Level != level;
        bool childItemKeysChanged = !this.ChildItemKeys.SequenceEqual(childItemKeys);

        this.Order = order;
        this.Level = level;
        this.ParentItemKey = parentItemKey;
        this.ChildItemKeys = childItemKeys.ToArray();

        if (levelChanged)
        {
            this.RaisePropertyChanged(nameof(this.Level));
            this.RaisePropertyChanged(nameof(this.IndentMargin));
        }

        if (childItemKeysChanged)
        {
            this.RaisePropertyChanged(nameof(this.ChildItemKeys));
            this.RaisePropertyChanged(nameof(this.HasChildren));
            this.RaisePropertyChanged(nameof(this.StickyRow));
            this.RaisePropertyChanged(nameof(this.ContentCornerRadius));
            this.RaisePropertyChanged(nameof(this.ExpandSectionCornerRadius));
            this.ToggleExpandCommand.RaiseCanExecuteChanged();
        }
    }

    private void OnNodePropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(EntityListNodeViewModel.DisplayName), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.DisplayName));
        }
        else if (string.Equals(e.PropertyName, nameof(EntityListNodeViewModel.EntityType), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.EntityType));
        }
        else if (string.Equals(e.PropertyName, nameof(EntityListNodeViewModel.HasChildren), StringComparison.Ordinal))
        {
            // #1232: a collapsed folder reports HasChildren via the node's lazy override before its
            // children are materialized. Propagate that so the expand affordance appears.
            this.RaisePropertyChanged(nameof(this.HasChildren));
            this.RaisePropertyChanged(nameof(this.StickyRow));
            this.RaisePropertyChanged(nameof(this.ContentCornerRadius));
            this.RaisePropertyChanged(nameof(this.ExpandSectionCornerRadius));
            this.ToggleExpandCommand.RaiseCanExecuteChanged();
        }
    }

    private void OnCardPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(EntityCardViewModel.FieldEditors), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.FieldEditors));
        }
        else if (string.Equals(e.PropertyName, nameof(EntityCardViewModel.DisplayName), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.DisplayName));
        }
        else if (string.Equals(e.PropertyName, nameof(EntityCardViewModel.EntityType), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.EntityType));
        }
        else if (string.Equals(e.PropertyName, nameof(EntityCardViewModel.IsEditMode), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.IsEditMode));
            this.RaisePropertyChanged(nameof(this.EditModeGlyph));
            this.RaisePropertyChanged(nameof(this.ShowEditIndicator));
            this.RaisePropertyChanged(nameof(this.ShowEditActions));
        }
        else if (string.Equals(e.PropertyName, nameof(EntityCardViewModel.EditModeGlyph), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.EditModeGlyph));
        }
        else if (string.Equals(e.PropertyName, nameof(EntityCardViewModel.ShowEditIndicator), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.ShowEditIndicator));
        }
        else if (string.Equals(e.PropertyName, nameof(EntityCardViewModel.ShowEditActions), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.ShowEditActions));
        }
        else if (string.Equals(e.PropertyName, nameof(EntityCardViewModel.ShowRawJsonEditor), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.ShowRawJsonEditor));
        }
        else if (string.Equals(e.PropertyName, nameof(EntityCardViewModel.ShowJsonButton), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.ShowJsonButton));
        }
        else if (string.Equals(e.PropertyName, nameof(EntityCardViewModel.ShowDeleteButton), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.ShowDeleteButton));
        }
        else if (string.Equals(e.PropertyName, nameof(EntityCardViewModel.HasShortcuts), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.HasShortcuts));
        }
        else if (string.Equals(e.PropertyName, nameof(EntityCardViewModel.IsDeleted), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.IsDeleted));
        }
        else if (string.Equals(e.PropertyName, nameof(EntityCardViewModel.IsInteractive), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.IsInteractive));
        }
        else if (string.Equals(e.PropertyName, nameof(EntityCardViewModel.ActivateShortcutCommand), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.ActivateShortcutCommand));
        }
        else if (string.Equals(e.PropertyName, nameof(EntityCardViewModel.IsRawJsonReadOnly), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.IsRawJsonReadOnly));
        }
        else if (string.Equals(e.PropertyName, nameof(EntityCardViewModel.RawJsonText), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.RawJsonText));
        }
        else if (string.Equals(e.PropertyName, nameof(EntityCardViewModel.JsonButtonText), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.JsonButtonText));
        }
    }
}
