using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private static readonly RelayCommand DisabledDeleteCommand = new(
        _ => { },
        _ => false);
    private IReadOnlyCollection<EntityFieldEditorViewModel>? editModeSnapshot;
    private string? editModeRawJsonSnapshot;
    private readonly SubscribedEntityViewModel? entity;
    private readonly string cardViewName;
    private readonly string displayName;
    private readonly string entityType;
    private IReadOnlyCollection<EntityFieldEditorViewModel> fieldEditors;
    private string rawJsonText;
    private readonly JsonValidationViewModel validation;
    private Action<EntityListNodeViewModel, bool>? onExpansionChanged;

    public EntityListNodeViewModel(
        SubscribedEntityViewModel entity,
        IReadOnlyList<string> nameComponents,
        string sortKey,
        IReadOnlyCollection<EntityFieldEditorViewModel>? fieldEditors = null,
        string? cardViewName = null,
        IEntitySchemaComposer? schemaComposer = null)
    {
        this.entity = entity;
        this.cardViewName = cardViewName ?? EntityCardViewResolver.RawViewName;
        this.displayName = entity.DisplayName;
        this.entityType = entity.EntityType;
        this.fieldEditors = fieldEditors ?? Array.Empty<EntityFieldEditorViewModel>();
        this.rawJsonText = BuildRawJsonText(entity.Data);
        this.validation = new JsonValidationViewModel(schemaComposer);
        this.SetFieldEditorEditMode(false);
        entity.PropertyChanged += this.OnEntityPropertyChanged;
        this.NameComponents = nameComponents;
        this.SortKey = sortKey;
        this.Badges = new BadgesViewModel(new BadgesModel());
        this.ToggleExpandCommand = new RelayCommand(
            _ => this.IsExpanded = !this.IsExpanded,
            _ => this.HasChildren);
        this.ToggleEditModeCommand = new RelayCommand(
            _ => this.EnterEditMode(),
            _ => !this.IsEditMode && (entity.CanEditEntity || this.FieldEditors.Count > 0));
        this.SaveEditModeCommand = new RelayCommand(
            _ => this.SaveEditMode(),
            _ => this.IsEditMode && this.Validation.IsValid);
        this.DiscardEditModeCommand = new RelayCommand(
            _ => this.DiscardEditMode(),
            _ => this.IsEditMode);
        this.ToggleJsonViewCommand = entity.ToggleRawJsonVisibilityCommand;
        this.DeleteEntityCommand = entity.DeleteEntityCommand;
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
        this.entity = null;
        this.displayName = displayName;
        this.entityType = entityType;
        this.cardViewName = cardViewName ?? EntityCardViewResolver.RawViewName;
        this.fieldEditors = fieldEditors ?? Array.Empty<EntityFieldEditorViewModel>();
        this.rawJsonText = string.Empty;
        this.validation = new JsonValidationViewModel();
        this.SetFieldEditorEditMode(false);
        this.NameComponents = nameComponents;
        this.SortKey = sortKey;
        this.Badges = new BadgesViewModel(new BadgesModel());
        this.ToggleExpandCommand = new RelayCommand(
            _ => this.IsExpanded = !this.IsExpanded,
            _ => this.HasChildren);
        this.ToggleEditModeCommand = new RelayCommand(
            _ => this.EnterEditMode(),
            _ => !this.IsEditMode && this.FieldEditors.Count > 0);
        this.SaveEditModeCommand = new RelayCommand(
            _ => this.SaveEditMode(),
            _ => this.IsEditMode && this.Validation.IsValid);
        this.DiscardEditModeCommand = new RelayCommand(
            _ => this.DiscardEditMode(),
            _ => this.IsEditMode);
        this.ToggleJsonViewCommand = new RelayCommand(
            _ => this.IsJsonVisible = !this.IsJsonVisible,
            _ => this.ShowJsonButton);
        this.DeleteEntityCommand = DisabledDeleteCommand;
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

    public RelayCommand DeleteEntityCommand { get; }

    public ObservableCollection<EntityShortcutViewModel> Shortcuts { get; } = [];

    public RelayCommand? ActivateShortcutCommand { get; private set; }

    public BadgesViewModel Badges { get; private set; }

    public RelayCommand? ToggleInterestCommand { get; private set; }

    public bool HasBadges => this.Badges.Badges.Count > 0;

    public bool HasShortcuts => this.Shortcuts.Count > 0;

    public bool IsDeleted => this.entity?.Deleted ?? false;

    public bool IsInteractive => !this.IsDeleted;

    public string DisplayName => this.entity?.DisplayName ?? this.displayName;

    public string EntityType => this.entity?.EntityType ?? this.entityType;

    public string CardViewName => this.cardViewName;

    public IReadOnlyCollection<EntityDisplayItemViewModel> DisplayItems => this.entity?.DisplayItems ?? Array.Empty<EntityDisplayItemViewModel>();

    public IReadOnlyCollection<EntityFieldEditorViewModel> FieldEditors => this.fieldEditors;

    public JsonValidationViewModel Validation => this.validation;

    public bool AreShortcutsEnabled => !this.IsEditMode;

    public bool AreBadgesEnabled => !this.IsEditMode;

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
            this.RaisePropertyChanged(nameof(this.AreShortcutsEnabled));
            this.RaisePropertyChanged(nameof(this.AreBadgesEnabled));
            this.RaisePropertyChanged(nameof(this.ShowJsonButton));
            this.SaveEditModeCommand.RaiseCanExecuteChanged();
        }
    }

    public string EditModeGlyph => this.IsEditMode ? "👁" : "✎";

    public bool ShowEditIndicator => !this.IsEditMode;

    public bool ShowEditActions => this.IsEditMode;

    public bool ShowJsonButton => (this.entity?.CanToggleRawJson ?? !string.IsNullOrWhiteSpace(this.rawJsonText))
        && (!this.HasShortcuts || this.IsEditMode);

    public bool ShowDeleteButton => (this.entity?.CanDeleteEntity ?? false)
        && !this.HasShortcuts;

    public bool IsRawJsonReadOnly => !this.IsEditMode;

    public bool ShowRawJsonEditor => this.IsJsonVisible;

    public bool IsJsonVisible
    {
        get => this.entity?.IsRawJsonVisible ?? this.isJsonVisible;
        set
        {
            if (this.entity is not null)
            {
                if (value != this.entity.IsRawJsonVisible)
                {
                    this.entity.ToggleRawJsonVisibility();
                }

                return;
            }

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
        set
        {
            if (!this.SetProperty(ref this.rawJsonText, value))
            {
                return;
            }

            if (this.IsEditMode)
            {
                _ = this.ValidateRawJsonAsync();
            }
        }
    }

    private async System.Threading.Tasks.Task ValidateRawJsonAsync()
    {
        await this.validation.UpdateAsync(this.rawJsonText);
        this.SaveEditModeCommand.RaiseCanExecuteChanged();
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

    public void SetFieldEditors(
        IReadOnlyCollection<EntityFieldEditorViewModel> fieldEditors)
    {
        this.fieldEditors = fieldEditors;
        this.SetFieldEditorEditMode(this.IsEditMode);
        this.RaisePropertyChanged(nameof(this.FieldEditors));
        this.ToggleEditModeCommand.RaiseCanExecuteChanged();
    }

    public void SetShortcuts(
        IReadOnlyCollection<EntityShortcutViewModel> shortcuts,
        RelayCommand activateShortcutCommand)
    {
        this.ActivateShortcutCommand = activateShortcutCommand;
        this.Shortcuts.Clear();
        foreach (var shortcut in shortcuts)
        {
            this.Shortcuts.Add(shortcut);
        }

        this.RaisePropertyChanged(nameof(this.HasShortcuts));
        this.RaisePropertyChanged(nameof(this.ActivateShortcutCommand));
        this.RaisePropertyChanged(nameof(this.ShowJsonButton));
        this.RaisePropertyChanged(nameof(this.ShowDeleteButton));
    }

    /// <summary>
    /// Sets the interest badges shown on the card and the command that toggles an interest on/off for
    /// this entity when a badge glyph is clicked.
    /// </summary>
    public void SetBadges(
        BadgesViewModel badges)
    {
        this.Badges = badges;
        this.ToggleInterestCommand = new RelayCommand(
            async parameter =>
            {
                if (parameter is BadgeModel badge && this.entity is not null)
                {
                    await this.entity.ToggleInterestAsync(badge.InterestTypeName);
                }
            },
            _ => this.entity is not null);

        this.RaisePropertyChanged(nameof(this.Badges));
        this.RaisePropertyChanged(nameof(this.ToggleInterestCommand));
        this.RaisePropertyChanged(nameof(this.HasBadges));
    }

    private void SetFieldEditorEditMode(
        bool isEditMode)
    {
        foreach (var fieldEditor in this.fieldEditors)
        {
            fieldEditor.SetEditMode(isEditMode);
        }
    }

    public void EnterEditMode()
    {
        if (this.IsEditMode)
        {
            return;
        }

        this.editModeSnapshot = this.fieldEditors.Select(static fieldEditor => fieldEditor.Clone()).ToArray();
        this.editModeRawJsonSnapshot = this.rawJsonText;
        this.IsEditMode = true;
        this.ToggleEditModeCommand.RaiseCanExecuteChanged();
        this.SaveEditModeCommand.RaiseCanExecuteChanged();
        this.DiscardEditModeCommand.RaiseCanExecuteChanged();
        this.RaisePropertyChanged(nameof(this.ShowEditIndicator));
        this.RaisePropertyChanged(nameof(this.ShowEditActions));
        this.RaisePropertyChanged(nameof(this.IsRawJsonReadOnly));
        _ = this.ValidateRawJsonAsync();
    }

    private async void SaveEditMode()
    {
        if (!this.IsEditMode)
        {
            return;
        }

        if (this.entity is not null)
        {
            JsonElement? parsed = null;
            try
            {
                using var document = JsonDocument.Parse(this.rawJsonText);
                parsed = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Invalid JSON should already block save via validation; ignore defensively only here.
                return;
            }

            if (parsed is JsonElement data)
            {
                await this.entity.SaveEditedEntityAsync(data);
            }
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

    private void OnEntityPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(SubscribedEntityViewModel.IsRawJsonVisible), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.IsJsonVisible));
            this.RaisePropertyChanged(nameof(this.ShowRawJsonEditor));
            this.RaisePropertyChanged(nameof(this.JsonButtonText));
            return;
        }

        if (string.Equals(e.PropertyName, nameof(SubscribedEntityViewModel.CanToggleRawJson), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.ShowJsonButton));
            return;
        }

        if (string.Equals(e.PropertyName, nameof(SubscribedEntityViewModel.CanDeleteEntity), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.ShowDeleteButton));
            return;
        }

        if (string.Equals(e.PropertyName, nameof(SubscribedEntityViewModel.CanEditEntity), StringComparison.Ordinal))
        {
            this.ToggleEditModeCommand.RaiseCanExecuteChanged();
            return;
        }

        if (string.Equals(e.PropertyName, nameof(SubscribedEntityViewModel.Deleted), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.IsDeleted));
            this.RaisePropertyChanged(nameof(this.IsInteractive));
        }
    }
}
