using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// The view model for an entity card (the content rendered by <c>EntityCardControl</c>). A card is
/// bound to a single <see cref="SubscribedEntityViewModel"/> and shows that entity's fields based on
/// the bound view selector (the entity-type-view resolved by <see cref="FieldEditorFactory"/>). It owns
/// the read/edit lifecycle, raw-JSON editing, validation, badges, shortcuts, and deletion. Hierarchy
/// (children, expand/collapse) is owned by <see cref="EntityListNodeViewModel"/>, not the card.
/// </summary>
public sealed class EntityCardViewModel : ViewModelBase
{
    private static readonly RelayCommand DisabledDeleteCommand = new(
        _ => { },
        _ => false);

    private readonly SubscribedEntityViewModel? entity;
    private readonly FieldEditorFactory? fieldEditorFactory;
    private readonly string cardViewName;
    private readonly string displayName;
    private readonly string entityType;
    private readonly JsonValidationViewModel validation;
    private readonly List<EntityDisplayItemViewModel> displayItems = [];
    private IReadOnlyCollection<EntityFieldEditorViewModel> fieldEditors;
    private ExternalEntityCardViewModel? externalCard;
    private string rawJsonText;
    private bool isEditMode;
    private bool isJsonVisible;
    private IReadOnlyCollection<EntityFieldEditorViewModel>? editModeSnapshot;
    private string? editModeRawJsonSnapshot;

    public EntityCardViewModel(
        SubscribedEntityViewModel entity,
        IReadOnlyCollection<EntityFieldEditorViewModel>? fieldEditors = null,
        string? cardViewName = null,
        IEntitySchemaComposer? schemaComposer = null,
        FieldEditorFactory? fieldEditorFactory = null)
    {
        this.entity = entity;
        this.fieldEditorFactory = fieldEditorFactory;
        this.cardViewName = cardViewName ?? EntityCardViewResolver.RawViewName;
        this.displayName = entity.DisplayName;
        this.entityType = entity.EntityType;
        this.fieldEditors = fieldEditors ?? Array.Empty<EntityFieldEditorViewModel>();
        this.rawJsonText = BuildRawJsonText(entity.Data);
        this.validation = new JsonValidationViewModel(schemaComposer);
        this.Badges = new BadgesViewModel(new BadgesModel());
        this.StatusBadges = new StatusBadgesViewModel(new StatusBadgesModel());
        this.SetFieldEditorEditMode(false);
        this.RefreshDisplayItems();
        entity.PropertyChanged += this.OnEntityPropertyChanged;
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
        this.externalCard = this.cardViewName == "external" ? ExternalEntityCardViewModel.Create(entity) : null;
        _ = this.BuildFieldEditorsAsync();
    }

    public EntityCardViewModel(
        string displayName,
        string entityType,
        IReadOnlyCollection<EntityFieldEditorViewModel>? fieldEditors = null,
        string? cardViewName = null)
    {
        this.entity = null;
        this.displayName = displayName;
        this.entityType = entityType;
        this.cardViewName = cardViewName ?? EntityCardViewResolver.RawViewName;
        this.fieldEditors = fieldEditors ?? Array.Empty<EntityFieldEditorViewModel>();
        this.rawJsonText = string.Empty;
        this.validation = new JsonValidationViewModel();
        this.Badges = new BadgesViewModel(new BadgesModel());
        this.StatusBadges = new StatusBadgesViewModel(new StatusBadgesModel());
        this.SetFieldEditorEditMode(false);
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
    }

    public SubscribedEntityViewModel? Entity => this.entity;

    public string DisplayName => this.entity?.DisplayName ?? this.displayName;

    public string EntityType => this.entity?.EntityType ?? this.entityType;

    public string CardViewName => this.cardViewName;

    public ExternalEntityCardViewModel? ExternalCard => this.externalCard;

    public IReadOnlyCollection<EntityDisplayItemViewModel> DisplayItems => this.displayItems;

    public IReadOnlyCollection<EntityFieldEditorViewModel> FieldEditors => this.fieldEditors;

    public JsonValidationViewModel Validation => this.validation;

    public BadgesViewModel Badges { get; private set; }

    public StatusBadgesViewModel StatusBadges { get; private set; }

    public ObservableCollection<EntityShortcutViewModel> Shortcuts { get; } = [];

    public RelayCommand? ActivateShortcutCommand { get; private set; }

    public RelayCommand? ToggleInterestCommand { get; private set; }

    public RelayCommand ToggleEditModeCommand { get; }

    public RelayCommand SaveEditModeCommand { get; }

    public RelayCommand DiscardEditModeCommand { get; }

    public RelayCommand ToggleJsonViewCommand { get; }

    public RelayCommand DeleteEntityCommand { get; }

    public bool HasBadges => this.Badges.Badges.Count > 0;

    public bool HasStatusBadges => this.StatusBadges.Badges.Count > 0;

    public bool HasShortcuts => this.Shortcuts.Count > 0;

    public bool IsDeleted => this.entity?.Deleted ?? false;

    public bool IsInteractive => !this.IsDeleted;

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

    public bool ShowJsonButton => this.entity?.CanToggleRawJson ?? !string.IsNullOrWhiteSpace(this.rawJsonText);

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

    /// <summary>
    /// Sets the status badges shown on the card. Status badges are display-only colored pills (one per
    /// annotated status field across the entity's entity types); they carry no command. The status
    /// badges are discovered asynchronously, so the card listens for collection changes to keep
    /// <see cref="HasStatusBadges"/> in sync as badges arrive.
    /// </summary>
    public void SetStatusBadges(
        StatusBadgesViewModel statusBadges)
    {
        this.StatusBadges.Badges.CollectionChanged -= this.OnStatusBadgesChanged;
        this.StatusBadges = statusBadges;
        this.StatusBadges.Badges.CollectionChanged += this.OnStatusBadgesChanged;
        this.RaisePropertyChanged(nameof(this.StatusBadges));
        this.RaisePropertyChanged(nameof(this.HasStatusBadges));
    }

    private void OnStatusBadgesChanged(
        object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        this.RaisePropertyChanged(nameof(this.HasStatusBadges));
    }

    /// <summary>
    /// Builds the entity's field editors (curated by its entity-type-view when one exists, otherwise
    /// all schema fields) and applies them to the card. Read-versus-edit presentation is embodied by
    /// each field editor, so the card renders correctly in both read and edit modes.
    /// </summary>
    private async Task BuildFieldEditorsAsync()
    {
        if (this.fieldEditorFactory is null
            || this.entity?.Data is not JsonElement entityData
            || entityData.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var built = await this.fieldEditorFactory
            .BuildFieldEditorsAsync(entityData, this.entity.EntityType)
            .ConfigureAwait(true);

        this.SetFieldEditors(built);
    }

    private void SetFieldEditorEditMode(
        bool isEditMode)
    {
        foreach (var fieldEditor in this.fieldEditors)
        {
            fieldEditor.IsEditMode = isEditMode;
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

    private async Task ValidateRawJsonAsync()
    {
        await this.validation.UpdateAsync(this.rawJsonText);
        this.SaveEditModeCommand.RaiseCanExecuteChanged();
    }

    private void RefreshDisplayItems()
    {
        this.displayItems.Clear();
        if (this.entity?.Snapshot.Data is JsonElement)
        {
            this.displayItems.AddRange(this.entity.DisplayItems);
        }
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

        if (string.Equals(e.PropertyName, nameof(SubscribedEntityViewModel.DisplayName), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(SubscribedEntityViewModel.EntityType), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.DisplayName));
            this.RaisePropertyChanged(nameof(this.EntityType));
            return;
        }

        if (string.Equals(e.PropertyName, nameof(SubscribedEntityViewModel.Snapshot), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.DisplayName));
            this.RaisePropertyChanged(nameof(this.EntityType));
            this.RefreshDisplayItems();
            this.RaisePropertyChanged(nameof(this.DisplayItems));
            if (!this.IsEditMode)
            {
                this.rawJsonText = BuildRawJsonText(this.entity?.Data);
                this.RaisePropertyChanged(nameof(this.RawJsonText));
                _ = this.BuildFieldEditorsAsync();
            }

            if (this.cardViewName == "external" && this.entity is not null)
            {
                this.externalCard = ExternalEntityCardViewModel.Create(this.entity);
                this.RaisePropertyChanged(nameof(this.ExternalCard));
            }

            return;
        }

        if (string.Equals(e.PropertyName, nameof(SubscribedEntityViewModel.Deleted), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.IsDeleted));
            this.RaisePropertyChanged(nameof(this.IsInteractive));
        }
    }
}
