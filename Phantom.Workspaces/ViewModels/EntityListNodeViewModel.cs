using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ViewModels;

public sealed class EntityListNodeViewModel : ViewModelBase
{
    private bool isExpanded;
    private bool isEditMode;
    private bool isJsonVisible;
    private IReadOnlyCollection<EntityFieldEditorViewModel>? editModeSnapshot;
    private string? editModeRawJsonSnapshot;
    private readonly SubscribedEntityViewModel? entity;
    private readonly string displayName;
    private readonly string entityType;
    private IReadOnlyCollection<EntityFieldEditorViewModel> fieldEditors;
    private string rawJsonText;

    public EntityListNodeViewModel(
        SubscribedEntityViewModel entity,
        IReadOnlyList<string> nameComponents,
        string sortKey,
        IReadOnlyCollection<EntityFieldEditorViewModel>? fieldEditors = null)
    {
        this.entity = entity;
        this.displayName = entity.DisplayName;
        this.entityType = entity.EntityType;
        this.fieldEditors = fieldEditors ?? Array.Empty<EntityFieldEditorViewModel>();
        this.rawJsonText = BuildRawJsonText(entity.Data);
        this.SetFieldEditorEditMode(false);
        this.NameComponents = nameComponents;
        this.SortKey = sortKey;
        this.ToggleExpandCommand = new RelayCommand(
            _ => this.IsExpanded = !this.IsExpanded,
            _ => this.HasChildren);
        this.ToggleEditModeCommand = new RelayCommand(
            _ => this.EnterEditMode(),
            _ => !this.IsEditMode && this.FieldEditors.Count > 0);
        this.SaveEditModeCommand = new RelayCommand(
            _ => this.SaveEditMode(),
            _ => this.IsEditMode);
        this.DiscardEditModeCommand = new RelayCommand(
            _ => this.DiscardEditMode(),
            _ => this.IsEditMode);
        this.ToggleJsonViewCommand = new RelayCommand(
            _ => this.IsJsonVisible = !this.IsJsonVisible,
            _ => this.ShowJsonButton);
    }

    public EntityListNodeViewModel(
        string displayName,
        string entityType,
        IReadOnlyList<string> nameComponents,
        string sortKey,
        IReadOnlyCollection<EntityFieldEditorViewModel>? fieldEditors = null,
        bool isExpanded = false)
    {
        this.entity = null;
        this.displayName = displayName;
        this.entityType = entityType;
        this.fieldEditors = fieldEditors ?? Array.Empty<EntityFieldEditorViewModel>();
        this.rawJsonText = string.Empty;
        this.SetFieldEditorEditMode(false);
        this.NameComponents = nameComponents;
        this.SortKey = sortKey;
        this.ToggleExpandCommand = new RelayCommand(
            _ => this.IsExpanded = !this.IsExpanded,
            _ => this.HasChildren);
        this.ToggleEditModeCommand = new RelayCommand(
            _ => this.EnterEditMode(),
            _ => !this.IsEditMode && this.FieldEditors.Count > 0);
        this.SaveEditModeCommand = new RelayCommand(
            _ => this.SaveEditMode(),
            _ => this.IsEditMode);
        this.DiscardEditModeCommand = new RelayCommand(
            _ => this.DiscardEditMode(),
            _ => this.IsEditMode);
        this.ToggleJsonViewCommand = new RelayCommand(
            _ => this.IsJsonVisible = !this.IsJsonVisible,
            _ => this.ShowJsonButton);
        this.isExpanded = isExpanded;
    }

    public SubscribedEntityViewModel? Entity => this.entity;

    public IReadOnlyList<string> NameComponents { get; }

    public string SortKey { get; }

    public ObservableCollection<EntityListNodeViewModel> Children { get; } = new();

    public ObservableCollection<EntityListNodeViewModel> VisibleChildren { get; } = new();

    public RelayCommand ToggleExpandCommand { get; }

    public RelayCommand ToggleEditModeCommand { get; }

    public RelayCommand SaveEditModeCommand { get; }

    public RelayCommand DiscardEditModeCommand { get; }

    public RelayCommand ToggleJsonViewCommand { get; }

    public string DisplayName => this.entity?.DisplayName ?? this.displayName;

    public string EntityType => this.entity?.EntityType ?? this.entityType;

    public IReadOnlyCollection<EntityDisplayItemViewModel> DisplayItems => this.entity?.DisplayItems ?? Array.Empty<EntityDisplayItemViewModel>();

    public IReadOnlyCollection<EntityFieldEditorViewModel> FieldEditors => this.fieldEditors;

    public bool IsEditMode
    {
        get => this.isEditMode;
        set
        {
            if (!this.SetProperty(ref this.isEditMode, value))
            {
                return;
            }

            this.SetFieldEditorEditMode(value);
            this.RaisePropertyChanged(nameof(this.EditModeGlyph));
        }
    }

    public string EditModeGlyph => this.IsEditMode ? "👁" : "✎";

    public bool ShowEditIndicator => !this.IsEditMode;

    public bool ShowEditActions => this.IsEditMode;

    public bool ShowJsonButton => !string.IsNullOrWhiteSpace(this.rawJsonText);

    public bool IsRawJsonReadOnly => !this.IsEditMode;

    public bool ShowRawJsonEditor => this.IsJsonVisible;

    public bool IsJsonVisible
    {
        get => this.isJsonVisible;
        set
        {
            if (!this.SetProperty(ref this.isJsonVisible, value))
            {
                return;
            }

            this.RaisePropertyChanged(nameof(this.ShowRawJsonEditor));
            this.RaisePropertyChanged(nameof(this.JsonButtonText));
        }
    }

    public string JsonButtonText => "{}";

    public string RawJsonText
    {
        get => this.rawJsonText;
        set => this.SetProperty(ref this.rawJsonText, value);
    }

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

    public void SetFieldEditors(
        IReadOnlyCollection<EntityFieldEditorViewModel> fieldEditors)
    {
        this.fieldEditors = fieldEditors;
        this.SetFieldEditorEditMode(this.IsEditMode);
        this.RaisePropertyChanged(nameof(this.FieldEditors));
        this.ToggleEditModeCommand.RaiseCanExecuteChanged();
    }

    private void SetFieldEditorEditMode(
        bool isEditMode)
    {
        foreach (var fieldEditor in this.fieldEditors)
        {
            fieldEditor.SetEditMode(isEditMode);
        }
    }

    private void EnterEditMode()
    {
        this.editModeSnapshot = this.fieldEditors.Select(static fieldEditor => fieldEditor.Clone()).ToArray();
        this.editModeRawJsonSnapshot = this.rawJsonText;
        this.IsEditMode = true;
        this.ToggleEditModeCommand.RaiseCanExecuteChanged();
        this.SaveEditModeCommand.RaiseCanExecuteChanged();
        this.DiscardEditModeCommand.RaiseCanExecuteChanged();
        this.RaisePropertyChanged(nameof(this.ShowEditIndicator));
        this.RaisePropertyChanged(nameof(this.ShowEditActions));
        this.RaisePropertyChanged(nameof(this.IsRawJsonReadOnly));
    }

    private void SaveEditMode()
    {
        if (!this.IsEditMode)
        {
            return;
        }

        this.editModeSnapshot = null;
        this.editModeRawJsonSnapshot = null;
        this.IsEditMode = false;
        this.ToggleEditModeCommand.RaiseCanExecuteChanged();
        this.SaveEditModeCommand.RaiseCanExecuteChanged();
        this.DiscardEditModeCommand.RaiseCanExecuteChanged();
        this.RaisePropertyChanged(nameof(this.ShowEditIndicator));
        this.RaisePropertyChanged(nameof(this.ShowEditActions));
        this.RaisePropertyChanged(nameof(this.IsRawJsonReadOnly));
    }

    private void DiscardEditMode()
    {
        if (!this.IsEditMode)
        {
            return;
        }

        if (this.editModeSnapshot is not null)
        {
            this.SetFieldEditors(this.editModeSnapshot.Select(static fieldEditor => fieldEditor.Clone()).ToArray());
        }
        if (this.editModeRawJsonSnapshot is not null)
        {
            this.RawJsonText = this.editModeRawJsonSnapshot;
        }

        this.IsEditMode = false;
        this.editModeSnapshot = null;
        this.editModeRawJsonSnapshot = null;
        this.ToggleEditModeCommand.RaiseCanExecuteChanged();
        this.SaveEditModeCommand.RaiseCanExecuteChanged();
        this.DiscardEditModeCommand.RaiseCanExecuteChanged();
        this.RaisePropertyChanged(nameof(this.ShowEditIndicator));
        this.RaisePropertyChanged(nameof(this.ShowEditActions));
        this.RaisePropertyChanged(nameof(this.IsRawJsonReadOnly));
    }

    private static string BuildRawJsonText(
        JsonElement? data)
    {
        if (data is not JsonElement element)
        {
            return string.Empty;
        }

        using var parsedDocument = JsonDocument.Parse(element.GetRawText());
        return JsonSerializer.Serialize(
            parsedDocument.RootElement,
            new JsonSerializerOptions
            {
                WriteIndented = true,
            });
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
