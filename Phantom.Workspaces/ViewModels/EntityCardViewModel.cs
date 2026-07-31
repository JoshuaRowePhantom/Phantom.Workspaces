using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
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

    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

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
    private string? matchQuery;
    private bool isSelected;
    private bool isEditMode;
    private bool isJsonVisible;
    private IReadOnlyCollection<EntityFieldEditorViewModel>? editModeSnapshot;
    private string? editModeRawJsonSnapshot;
    private MainWindowViewModel? shortcutMainWindowViewModel;
    private ShortcutManager? shortcutManager;
    private IReadOnlyList<EntityShortcutViewModel> shortcuts = Array.Empty<EntityShortcutViewModel>();
    private CancellationTokenSource? shortcutResolutionCts;

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
        Lifetime.Run(this.BuildFieldEditorsAsync);
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

    /// <summary>
    /// The shortcuts currently applicable to this card. Reassigned wholesale on every resolution
    /// so the ItemsControl rebinds atomically (fix #1144 — the previous in-place Clear()/Add loop
    /// duplicated shortcuts when overlapping resolution runs interleaved).
    /// </summary>
    public IReadOnlyList<EntityShortcutViewModel> Shortcuts
    {
        get => this.shortcuts;
        private set
        {
            if (!this.SetProperty(ref this.shortcuts, value))
            {
                return;
            }

            this.RaisePropertyChanged(nameof(this.HasShortcuts));
            this.RaisePropertyChanged(nameof(this.ShowJsonButton));
            this.RaisePropertyChanged(nameof(this.ShowDeleteButton));
        }
    }

    public RelayCommand? ActivateShortcutCommand { get; private set; }

    public ICommand? ToggleInterestCommand { get; private set; }

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

    /// <summary>
    /// Currently active find query used to compute the highlight run over <see cref="DisplayName"/>
    /// and <see cref="EntityType"/>. Empty when no find session is active.
    /// </summary>
    public string? MatchQuery
    {
        get => this.matchQuery;
        set
        {
            var normalized = string.IsNullOrEmpty(value) ? null : value;
            if (!this.SetProperty(ref this.matchQuery, normalized))
            {
                return;
            }

            this.RaisePropertyChanged(nameof(this.IsFindMatch));
            this.RaisePropertyChanged(nameof(this.DisplayNameBefore));
            this.RaisePropertyChanged(nameof(this.DisplayNameMatch));
            this.RaisePropertyChanged(nameof(this.DisplayNameAfter));
            this.RaisePropertyChanged(nameof(this.EntityTypeBefore));
            this.RaisePropertyChanged(nameof(this.EntityTypeMatch));
            this.RaisePropertyChanged(nameof(this.EntityTypeAfter));
            this.RaisePropertyChanged(nameof(this.DisplayNameMatchStart));
            this.RaisePropertyChanged(nameof(this.DisplayNameMatchLength));
        }
    }

    /// <summary>
    /// True while this card is the currently-active find selection. Also used by find navigation to
    /// track which card should be brought into view.
    /// </summary>
    public bool IsSelected
    {
        get => this.isSelected;
        set => this.SetProperty(ref this.isSelected, value);
    }

    /// <summary>
    /// True while the current <see cref="MatchQuery"/> is non-empty and matches somewhere in the
    /// card text (used to gate the yellow-background inline run).
    /// </summary>
    public bool IsFindMatch => !string.IsNullOrEmpty(this.matchQuery)
        && (this.DisplayNameMatchStart >= 0 || this.EntityTypeMatchStart >= 0);

    /// <summary>
    /// True when the current <see cref="MatchQuery"/> matches only inside the entity's JSON values
    /// (not the visible card text). Set by <see cref="FindViewModel"/>.
    /// </summary>
    public bool MatchInJson { get; set; }

    public int DisplayNameMatchStart => FindMatchIndex(this.DisplayName, this.matchQuery);

    public int DisplayNameMatchLength => this.matchQuery?.Length ?? 0;

    public string DisplayNameBefore => SliceBefore(this.DisplayName, this.DisplayNameMatchStart);

    public string DisplayNameMatch => SliceMatch(this.DisplayName, this.DisplayNameMatchStart, this.DisplayNameMatchLength);

    public string DisplayNameAfter => SliceAfter(this.DisplayName, this.DisplayNameMatchStart, this.DisplayNameMatchLength);

    public int EntityTypeMatchStart => FindMatchIndex(this.EntityType, this.matchQuery);

    public int EntityTypeMatchLength => this.matchQuery?.Length ?? 0;

    public string EntityTypeBefore => SliceBefore(this.EntityType, this.EntityTypeMatchStart);

    public string EntityTypeMatch => SliceMatch(this.EntityType, this.EntityTypeMatchStart, this.EntityTypeMatchLength);

    public string EntityTypeAfter => SliceAfter(this.EntityType, this.EntityTypeMatchStart, this.EntityTypeMatchLength);

    /// <summary>
    /// Joined label of every non-abstract entity type the entity declares (issue #1164). A tool+note
    /// entity shows both "tool" and "note" here rather than only the primary type. When no subscribed
    /// entity is bound (the display-only constructor), falls back to the single supplied entity type.
    /// </summary>
    public string EntityTypeLabels => this.entity is null
        ? this.entityType
        : string.Join(", ", this.entity.NonAbstractEntityTypeNames);

    private static int FindMatchIndex(string? text, string? query)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query))
        {
            return -1;
        }

        return text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
    }

    private static string SliceBefore(string? text, int matchStart)
    {
        if (string.IsNullOrEmpty(text) || matchStart < 0)
        {
            return text ?? string.Empty;
        }

        return text.Substring(0, matchStart);
    }

    private static string SliceMatch(string? text, int matchStart, int matchLength)
    {
        if (string.IsNullOrEmpty(text) || matchStart < 0 || matchLength <= 0)
        {
            return string.Empty;
        }

        return text.Substring(matchStart, matchLength);
    }

    private static string SliceAfter(string? text, int matchStart, int matchLength)
    {
        if (string.IsNullOrEmpty(text) || matchStart < 0)
        {
            return string.Empty;
        }

        int end = matchStart + matchLength;
        if (end >= text.Length)
        {
            return string.Empty;
        }

        return text.Substring(end);
    }

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
                Lifetime.Run(this.ValidateRawJsonAsync);
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
        this.Shortcuts = shortcuts.ToList();
        this.RaisePropertyChanged(nameof(this.ActivateShortcutCommand));
    }

    /// <summary>
    /// Supplies the card the context it needs to resolve its own shortcuts: the
    /// <see cref="MainWindowViewModel"/> (which provides handler applicability and the
    /// <c>ActivateShortcutCommand</c>) and the <see cref="ViewModels.ShortcutManager"/>. Assigning the
    /// context wires <see cref="ActivateShortcutCommand"/> and re-resolves the shortcuts, so every card
    /// path (tree, single-entity view, etc.) is self-sufficient without an external push.
    /// </summary>
    public void SetShortcutContext(
        MainWindowViewModel mainWindowViewModel,
        ShortcutManager shortcutManager)
    {
        this.shortcutMainWindowViewModel = mainWindowViewModel;
        this.shortcutManager = shortcutManager;
        this.ActivateShortcutCommand = mainWindowViewModel.ActivateShortcutCommand;
        this.RaisePropertyChanged(nameof(this.ActivateShortcutCommand));
        this.QueueShortcutResolution();
    }

    /// <summary>
    /// Resolves the card's shortcuts from the current entity and shortcut context, then
    /// atomically reassigns <see cref="Shortcuts"/>. Fix #1144 — every resolution builds a fresh
    /// local list, checks <paramref name="ct"/> before assigning, and marshals the assignment
    /// (which raises PropertyChanged observed by the bound ItemsControl) onto the UI thread.
    /// No-op when no shortcut context or entity is available.
    /// </summary>
    public async Task ResolveShortcutsAsync(CancellationToken ct = default)
    {
        if (this.shortcutMainWindowViewModel is not { } mainWindowViewModel
            || this.shortcutManager is not { } manager
            || this.entity is null)
        {
            return;
        }

        var resolved = new List<EntityShortcutViewModel>();
        await foreach (var shortcut in manager
            .GetShortcutsForAsync(mainWindowViewModel, this.entity)
            .WithCancellation(ct)
            .ConfigureAwait(false))
        {
            resolved.Add(new EntityShortcutViewModel
            {
                Shortcut = shortcut,
                Entity = this.entity,
                ShortcutManager = manager,
            });
        }

        if (ct.IsCancellationRequested)
        {
            return;
        }

        // Atomic single-reference swap on the UI thread. No await between build and assignment.
        if (Dispatcher.UIThread.CheckAccess())
        {
            this.Shortcuts = resolved;
        }
        else
        {
            await Dispatcher.UIThread.InvokeAsync(() => this.Shortcuts = resolved);
        }
    }

    private void QueueShortcutResolution()
    {
        if (this.shortcutMainWindowViewModel is null
            || this.shortcutManager is null
            || this.entity is null)
        {
            return;
        }

        // Supersession guard (#1144): cancel any in-flight resolution so a stale older-snapshot
        // run can't finish last and assign a superseded shortcut set.
        this.shortcutResolutionCts?.Cancel();
        this.shortcutResolutionCts?.Dispose();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(this.Lifetime.Token);
        this.shortcutResolutionCts = linked;

        Lifetime.Run(_ => this.ResolveShortcutsAsync(linked.Token));
    }

    /// <summary>
    /// Sets the interest badges shown on the card and the command that toggles an interest on/off for
    /// this entity when a badge glyph is clicked.
    /// </summary>
    public void SetBadges(
        BadgesViewModel badges)
    {
        this.Badges = badges;
        this.ToggleInterestCommand = new AsyncRelayCommand(
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
    private async Task BuildFieldEditorsAsync(CancellationToken ct = default)
    {
        if (this.fieldEditorFactory is null
            || this.entity?.Data is not JsonElement entityData
            || entityData.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        // Issue #1164: pass every non-abstract entity type so the factory can compose per-type
        // presentations (e.g. a tool+note entity contributes the note's content field via the note
        // entity-type-view, not just the primary "tool" type).
        var entityTypeNames = this.entity.NonAbstractEntityTypeNames;
        var built = await this.fieldEditorFactory
            .BuildFieldEditorsAsync(entityData, entityTypeNames)
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
        Lifetime.Run(this.ValidateRawJsonAsync);
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

    private async Task ValidateRawJsonAsync(CancellationToken ct = default)
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
        return JsonSerializer.Serialize(parsedDocument.RootElement, IndentedJsonOptions);
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
            this.RaisePropertyChanged(nameof(this.EntityTypeLabels));
            return;
        }

        if (string.Equals(e.PropertyName, nameof(SubscribedEntityViewModel.Snapshot), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.DisplayName));
            this.RaisePropertyChanged(nameof(this.EntityType));
            this.RaisePropertyChanged(nameof(this.EntityTypeLabels));
            this.RefreshDisplayItems();
            this.RaisePropertyChanged(nameof(this.DisplayItems));
            if (!this.IsEditMode)
            {
                this.rawJsonText = BuildRawJsonText(this.entity?.Data);
                this.RaisePropertyChanged(nameof(this.RawJsonText));
                Lifetime.Run(this.BuildFieldEditorsAsync);
            }

            if (this.cardViewName == "external" && this.entity is not null)
            {
                this.externalCard = ExternalEntityCardViewModel.Create(this.entity);
                this.RaisePropertyChanged(nameof(this.ExternalCard));
            }

            // Re-resolve shortcuts for the new data (applicability can change with the snapshot).
            this.QueueShortcutResolution();

            return;
        }

        if (string.Equals(e.PropertyName, nameof(SubscribedEntityViewModel.Deleted), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.IsDeleted));
            this.RaisePropertyChanged(nameof(this.IsInteractive));
        }
    }
}
