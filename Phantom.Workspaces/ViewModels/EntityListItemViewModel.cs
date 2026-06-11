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

    public int? StickyRow => this.HasChildren ? this.Level : null;

    public RelayCommand ToggleExpandCommand { get; }

    public Thickness IndentMargin => new(this.Level * 22, 0, 0, 4);

    public string DisplayName => this.Node.DisplayName;

    public string EntityType => this.Node.EntityType;

    public IReadOnlyCollection<EntityDisplayItemViewModel> DisplayItems => this.Node.DisplayItems;

    public IReadOnlyCollection<EntityFieldEditorViewModel> FieldEditors => this.Node.FieldEditors;

    public bool IsEditMode => this.Node.IsEditMode;

    public RelayCommand ToggleEditModeCommand => this.Node.ToggleEditModeCommand;

    public RelayCommand SaveEditModeCommand => this.Node.SaveEditModeCommand;

    public RelayCommand DiscardEditModeCommand => this.Node.DiscardEditModeCommand;

    public string EditModeGlyph => this.Node.EditModeGlyph;

    public bool ShowEditIndicator => this.Node.ShowEditIndicator;

    public bool ShowEditActions => this.Node.ShowEditActions;

    public RelayCommand ToggleJsonViewCommand => this.Node.ToggleJsonViewCommand;

    public bool ShowJsonButton => this.Node.ShowJsonButton;

    public bool ShowRawJsonEditor => this.Node.ShowRawJsonEditor;

    public bool IsRawJsonReadOnly => this.Node.IsRawJsonReadOnly;

    public string JsonButtonText => this.Node.JsonButtonText;

    public string RawJsonText
    {
        get => this.Node.RawJsonText;
        set => this.Node.RawJsonText = value;
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

    private void OnNodePropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(EntityListNodeViewModel.FieldEditors), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.FieldEditors));
        }
        else if (string.Equals(e.PropertyName, nameof(EntityListNodeViewModel.IsEditMode), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.IsEditMode));
            this.RaisePropertyChanged(nameof(this.EditModeGlyph));
            this.RaisePropertyChanged(nameof(this.ShowEditIndicator));
            this.RaisePropertyChanged(nameof(this.ShowEditActions));
        }
        else if (string.Equals(e.PropertyName, nameof(EntityListNodeViewModel.EditModeGlyph), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.EditModeGlyph));
        }
        else if (string.Equals(e.PropertyName, nameof(EntityListNodeViewModel.ShowEditIndicator), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.ShowEditIndicator));
        }
        else if (string.Equals(e.PropertyName, nameof(EntityListNodeViewModel.ShowEditActions), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.ShowEditActions));
        }
        else if (string.Equals(e.PropertyName, nameof(EntityListNodeViewModel.ShowRawJsonEditor), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.ShowRawJsonEditor));
        }
        else if (string.Equals(e.PropertyName, nameof(EntityListNodeViewModel.ShowJsonButton), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.ShowJsonButton));
        }
        else if (string.Equals(e.PropertyName, nameof(EntityListNodeViewModel.IsRawJsonReadOnly), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.IsRawJsonReadOnly));
        }
        else if (string.Equals(e.PropertyName, nameof(EntityListNodeViewModel.RawJsonText), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.RawJsonText));
        }
        else if (string.Equals(e.PropertyName, nameof(EntityListNodeViewModel.JsonButtonText), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.JsonButtonText));
        }
    }
}
