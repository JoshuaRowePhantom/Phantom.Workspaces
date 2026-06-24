# Entity editor design

## Goal

Enable in-place editing of entities from their entity card. Today the "Edit" (`✎`)
button on a card is rendered but effectively disabled because the card never has
field editors to enter edit mode with, and there is no `EditEntityShortcut` that
lets the card decide whether editing is allowed. This design wires the Edit button
to an `EditEntityShortcut`, drives the card into an editing experience that mirrors
the entity-browser card (field list + JSON), live-validates the edited JSON against
the available schemas, and persists the result.

This document also adds a `Clone` shortcut that opens a clone editor tab seeded
from the source entity, with optional relationship cloning.

This document also covers two supporting data-model changes:

- An optional `entity-display-order` (float) on the **entity-type** entity, used to
  order the fields contributed by each of an entity's entity types.
- Rendering an entity by the union of fields across **all** of its entity types
  (not just one), ordered by `entity-display-order`.

## User-facing behavior

1. **Edit button.** Each entity card shows the `✎` Edit button. It is enabled only
   when the `EditEntityShortcut` applies to the entity (the entity is editable: it
   has a data snapshot, is not deleted, and the data-access layer permits update).
   Clicking it enters edit mode.
2. **Clone button.** Each entity card also shows a `Clone` shortcut (for cloneable
   entities). Clicking it opens a dedicated clone editor **tab** prepopulated from the
   source entity:
   - A freshly generated `entity-id` is prefilled.
   - Entity properties/data are copied identically from the source entity.
   - Entity names are prefilled with a derived "copy" name set (see clone behavior).
   - Relationships are listed as selectable items so the user can choose which
     relationships to clone.
   - **Save** creates the new entity (and selected cloned relationships).
   - **Cancel** closes the clone tab and creates nothing.
3. **Edit mode chrome.**
   - While editing, every other shortcut button and every interest badge on the card
     is disabled (greyed, non-interactive).
   - The JSON toggle button (`{}`) is **enabled** while editing (it is the one control,
     besides Save/Discard, that stays live) so the user can switch between the field
     list and the raw JSON editor.
   - Save (`💾`) and Discard (`✖`) actions appear.
4. **Live JSON validation.** While editing, the working JSON is continuously validated
   on every change against (a) JSON syntax and (b) the composed schema for the entity's
   entity types. A validation status line is shown:
   - Valid: the text `👍 Valid` in normal (non-error) styling.
   - Invalid: a red `(!)` followed by the validation error text (syntax error message,
     or the first schema evaluation error).
   - Save is disabled while the JSON is invalid.
5. **Field list (non-JSON mode).** When not in JSON mode, the entity snapshot is turned
   into an editable field list "as per the current view": the same field selection and
   display formats the current view's `entity-type-view` defines (falling back to the
   full schema field set when the view does not constrain fields).
6. **Expand-to-all-fields.** A `>` expander is added to the card (matching the
   entity-browser card). Collapsed, the card shows the view's curated fields; expanded,
   it shows **all** fields of the entity (the entity-browser rendering).
7. **Editable fields.** Every field editor in the list is editable in edit mode (text
   boxes, locale editors, MIME editors, nested object/array editors), reusing the
   existing `EntityFieldEditorViewModel` hierarchy.
8. **Default expander state.** Expanders within the field list start **expanded** by
   default, **except** localized-content sub-entries whose locale is neither the current
   UI locale nor `default` (those collapse by default to keep non-relevant locales out of
   the way). This applies to `local-string` "Other locales" expanders and
   `mime-attachment` localized maps.

## Clone shortcut behavior

The clone flow is draft-first and save-gated:

1. Clicking `Clone` opens a dedicated clone editor tab/view-model.
2. The tab edits a **draft clone** only; no entity is written on open.
3. Save writes the clone; cancel/dispose writes nothing.

### Draft initialization

When the draft is created:

1. `entity-id` is generated (GUID) immediately and shown/editable in the tab.
2. Data/properties are copied from the source entity snapshot.
3. Names are derived from the source names:
   - keep all leading name components,
   - derive the terminal component by appending `-copy`,
   - if that name already exists, append/increment a numeric suffix
     (`-copy-2`, `-copy-3`, ...).

### Optional relationship cloning

The clone editor tab shows direct relationships involving the source entity as selectable
items (checkbox list), including relationship type/name and participant summary.

On save:

1. Always create the cloned entity with the draft `entity-id`, names, and data.
2. For each selected relationship, create a cloned relationship entity:
   - copy the relationship data/properties,
   - generate a new relationship `entity-id`,
   - rewrite any participant reference equal to the source entity id to the clone id,
   - keep other participant references unchanged.
3. Submit entity creation + selected relationship creations in one update operation when
   supported so clone save is atomic from the user's perspective.

## Data-model changes

### `entity-display-order` on entity-type

`JsonSchemas/entity-type.json` gains an optional property:

```json
"entity-display-order": {
  "type": "number",
  "description": "Optional float that orders this entity type's contributed fields relative to other entity types when an entity is rendered or edited. Lower values are rendered first. When absent, the type sorts after all types that specify a value, then by type name."
}
```

Every existing entity-type entity (the `schema-definitions/*-entity-type.json` files)
is given a **unique** `entity-display-order`. Uniqueness is the only hard requirement;
values are spaced (10, 20, 30, …) so new types can be inserted between existing ones
without renumbering. Foundational/base types (entity, note, actionable, …) sort first;
domain types follow.

### Per-field ordering: `x-absolute-entity-display-order` and `x-relative-entity-display-order`

Individual field schemas may declare ordering hints:

```json
"x-absolute-entity-display-order": {
  "type": "number",
  "description": "Optional float. Fields with an absolute order are sorted strictly by this value and rendered before all other fields and entities, regardless of which entity type contributes them."
},
"x-relative-entity-display-order": {
  "type": "number",
  "description": "Optional float. Orders a field within its own entity type's field group. Defaults to 0 when absent."
}
```

- **Absolute-ordered fields** (those whose schema sets `x-absolute-entity-display-order`)
  appear absolutely ordered and **before all other fields and entities**. They are not
  grouped by entity type.
- **`x-relative-entity-display-order`** orders a field **within its own entity type's**
  field group.

### Rendering all entity types' fields

An entity declares multiple `entity-types`. When the card builds its field list, it
unions the fields contributed by **all** of those entity types and sorts each field by a
constructed tuple:

```
(absolute, entity-type-name, relative, name)
```

where:

- `absolute` = the field's `x-absolute-entity-display-order` when present; otherwise the
  contributing entity type's `entity-display-order` value (so fields without an explicit
  absolute order fall back to their type's order, while absolute-ordered fields can float
  ahead of everything).
- `entity-type-name` = the contributing entity type's name (groups a type's fields
  together and breaks ties between types sharing an `absolute` value).
- `relative` = the field's `x-relative-entity-display-order`, defaulting to `0`.
- `name` = the field name (final, stable tiebreaker).

Today `FieldEditorFactory.BuildFieldEditorsAsync` resolves a single `entityTypeName`. It
is extended to:

1. Read the entity's `entity-types` array.
2. Resolve each type's `entity-type-view` (curated fields) — or schema field set when
   expanded / when no view exists — recording, per field, the contributing entity type's
   `entity-display-order` and the field's `x-absolute-`/`x-relative-entity-display-order`.
3. Build the `(absolute, entity-type-name, relative, name)` tuple for every field.
4. Sort by that tuple and de-duplicate by field-path so a field shared by two types
   appears once (the first occurrence in sorted order wins).

## Editable display name

The entity card's display-name text is made directly editable for fast renaming:

- **Click to edit.** The display name is rendered as read-only text by default. Clicking
  (or tab-focusing) it turns it into an editable `TextBox` seeded with the current display
  name.
- **Save on focus loss.** When focus leaves the text box (blur) — or on Enter — the new
  display name is written back to the entity automatically and persisted through the
  data-access layer. Escape cancels and restores the previous value without saving.
- **No save button.** This is an inline, single-field convenience editor independent of
  the card's full edit mode; it does not require entering edit mode and does not toggle
  the edit-mode chrome.

### Code changes

- **`EntityListNodeViewModel`** (and/or a small `EditableDisplayNameViewModel` it owns):
  add `bool IsEditingDisplayName`, `string DisplayNameDraft`, `BeginEditDisplayName()`,
  `CommitDisplayName()` (calls a new `SubscribedEntityViewModel.SaveDisplayNameAsync`),
  and `CancelDisplayName()`.
- **`SubscribedEntityViewModel.SaveDisplayNameAsync(string)`**: merges the new
  `display-name` (current-locale aware, consistent with `local-string` locale handling)
  into the entity data and pushes the update asynchronously.
- **`EntityCardControl.axaml`**: the title `TextBlock` toggles with an editing `TextBox`
  (bound to `DisplayNameDraft`), with `LostFocus` → commit and key bindings for Enter
  (commit) / Escape (cancel). Prefer a shared style class for the editable-title affordance
  over inline styling.

### Tests

- Beginning edit seeds the draft from the current display name.
- Commit (blur/Enter) calls `SaveDisplayNameAsync` with the new value and persists.
- Cancel (Escape) restores the original value and does **not** persist.
- Locale-aware write targets the current UI locale entry (or `default`).

## Entity-id field selector

Fields whose resolved type is an entity reference (`entity-id`) are shown and edited via a
dedicated selector rather than a raw string box.

### Display (read mode)

- The field displays the **display name** of the referenced entity (resolved
  asynchronously from the entity-id), not the raw id.
- A mouseover/tooltip shows the referenced entity's **names** and **entity id**.

### Selection (edit mode)

- The field is a drop-down selector with a **search box**.
- Typing in the search box queries candidates using the **vector search** capability
  (`EntityVectorQueryClause`), consistent with the data-access query API (there is no
  full-text clause; semantic relevance uses vector search).
- Each result row shows the candidate's **display name**, its **entity names**, and its
  **entity id**, each rendered as a **copyable text-box item** (read-only `TextBox`/
  selectable text so the user can copy any of them).
- Choosing a result sets the field's value to that entity's id.

### Code changes

- **`ResolvedFieldType`** already exposes `EntityTypes` (from `x-entity-types`); the
  factory uses a non-empty `EntityTypes` (and/or an `entity-id` `TypeName`) to select the
  entity-reference editor.
- **New `EntityReferenceFieldEditorViewModel : EntityFieldEditorViewModel`**:
  - Holds the selected `entity-id`, the resolved display name, and the tooltip text
    (names + id).
  - `string SearchText` with an async, debounced-by-event (not timer) `SearchAsync` that
    runs an `EntityVectorQueryClause` constrained to the field's allowed `EntityTypes`.
  - `ObservableCollection<EntityReferenceCandidateViewModel> Results`, each exposing
    `DisplayName`, `Names`, `EntityId` as copyable strings and a `SelectCommand`.
  - Resolves the current value's display name on construction (async).
  - Participates in `SetEditMode`/`Clone`.
- **`FieldEditorFactory.CreateFieldEditorAsync`**: add an entity-reference branch (before
  the default) that constructs `EntityReferenceFieldEditorViewModel`, injecting an
  `IEntityReferenceSearch` abstraction over the data-access layer's vector query so it is
  unit-testable.
- **`EntityCardControl.axaml` / field-editor templates**: a `DataTemplate` for
  `EntityReferenceFieldEditorViewModel` — read-mode label with tooltip, edit-mode
  drop-down with the search box and copyable result rows. Prefer shared style classes.

### Tests

- Read mode shows the referenced entity's display name; tooltip contains names + id.
- Typing search text issues an `EntityVectorQueryClause` scoped to the field's
  `EntityTypes` and populates `Results`.
- Result rows expose display name, names, and id as copyable items.
- Selecting a result updates the field's `entity-id` value.
- Search is event-driven/deterministic (no timing-based waits).
- An entity-reference field is detected from `x-entity-types` / `entity-id` type.

## x-field-editor extension

A schema (for a field, or for a field's type) may declare a custom editor view model:

```json
"x-field-editor": "Phantom.Workspaces.ViewModels.MyCustomFieldEditorViewModel, Phantom.Workspaces"
```

`x-field-editor` is **either** a short name for a well-known editor view model **or** an
**assembly-qualified type name** of a class deriving from `EntityFieldEditorViewModel`.
When present on the resolved field schema (or the field-type's schema),
`FieldEditorFactory.CreateFieldEditorAsync` instantiates that type instead of the built-in
editor selected by `TypeName`. Resolution rules:

1. `ResolvedFieldType` is extended with `string? FieldEditorTypeName`, read from the
   schema node's `x-field-editor` (mirroring how `x-default-mime-type` /
   `x-entity-types` are read in `FieldTypeResolver.Read*`).
2. **Short-name resolution first.** `CustomFieldEditorActivator` maintains a registry of
   well-known short names mapped to built-in editor view-model types, so common schemas
   can write a stable, assembly-independent token instead of a fragile assembly-qualified
   name. Examples:

   ```json
   "x-field-editor": "string"
   "x-field-editor": "local-string"
   "x-field-editor": "mime-attachment"
   "x-field-editor": "markdown"
   "x-field-editor": "json-schema"
   "x-field-editor": "entity-reference"
   ```

   Short names are matched **case-sensitively** against the registry (values stored in
   data/schemas are matched case-sensitively; only user-typed input is treated
   case-insensitively elsewhere). For example `entity-reference` →
   `EntityReferenceFieldEditorViewModel`, `markdown` →
   `MarkdownMimeAttachmentFieldEditorViewModel`. The registry is the single source of
   truth and is unit-tested for completeness.
3. **Assembly-qualified fallback.** If the value is not a registered short name,
   `CustomFieldEditorActivator` treats it as an assembly-qualified type name and resolves
   the `System.Type` via `Type.GetType(name, throwOnError: false)`, constructing it through
   a small factory contract so the editor receives the field name, current value, and
   resolved type.
4. **Failure fallback.** Unknown short names and unloadable/invalid type names fall back to
   the default editor for the `TypeName` (no defensive swallowing beyond the documented
   fallback — an unresolvable `x-field-editor` is a configuration error surfaced via
   logging).
5. Custom editors participate in the same `SetEditMode`/`Clone` lifecycle as built-ins.

## Architecture / code changes

### New: `EditEntityShortcut` + `CloneEntityShortcut` + handlers

- **`Shortcut.Edit`** (`Phantom.Workspaces/ViewModels/Shortcut.cs`): new static
  `Shortcut("Edit", "✎")` with hover text "Edit entity"; added to
  `ShortcutManager.shortcuts`.
- **`Shortcut.Clone`** (`Phantom.Workspaces/ViewModels/Shortcut.cs`): new static
  `Shortcut("Clone", "⧉")` with hover text "Clone entity"; added to
  `ShortcutManager.shortcuts`.
- **`EditEntityShortcutHandler`** (new,
  `Phantom.Workspaces/ViewModels/EditEntityShortcutHandler.cs`): derives from
  `ShortcutHandler`.
  - `ShouldApplyTo` returns `true` when `shortcut == Shortcut.Edit` and
    `entityViewModel.CanEditEntity`.
  - `Handle` invokes the card node's `EnterEditMode` for the entity (routed through the
    `MainWindowViewModel`/owning node) and returns `true`.
- **`CloneEntityShortcutHandler`** (new,
  `Phantom.Workspaces/ViewModels/CloneEntityShortcutHandler.cs`): derives from
  `ShortcutHandler`.
  - `ShouldApplyTo` returns `true` when `shortcut == Shortcut.Clone` and
    `entityViewModel.CanEditEntity` (clone requires create/update capability and snapshot data).
  - `Handle` opens `CloneEntityEditorViewModel` in a new tab and returns `true`.
  - The handler does not write data directly; writes occur only from clone-tab Save.
  - Registered in `MainWindowViewModel` alongside the other handlers
    (`AddShortcutHandler(new EditEntityShortcutHandler())`,
    `AddShortcutHandler(new CloneEntityShortcutHandler())`).
- **`SubscribedEntityViewModel`** (`.../SubscribedEntityViewModel.cs`): add
  `bool CanEditEntity` (data is a `JsonElement`, not deleted, and update is supported by
  the data-access layer), raising `PropertyChanged` from the same places that raise
  `CanToggleRawJson`/`CanDeleteEntity`. Add `Task SaveEditedEntityAsync(JsonElement data)`
  that pushes the merged update through the data-access layer.

### New: clone editor view model + tab

- **`CloneEntityEditorViewModel`** (new,
  `Phantom.Workspaces/ViewModels/CloneEntityEditorViewModel.cs`):
  - Holds clone draft state (`CloneEntityId`, `CloneNames`, copied data/properties).
  - Exposes `ObservableCollection<CloneRelationshipSelectionItemViewModel>`
    for relationship checkboxes.
  - Exposes `SaveCloneCommand` / `CancelCommand`.
  - `SaveCloneCommand` builds create/update changes for clone entity + selected relationships.
  - `CancelCommand` closes the tab with no persistence.
- **`CloneRelationshipSelectionItemViewModel`** (new):
  - `bool IsSelected`, relationship identity, display label, and participant summary.
- **`CloneEntityWorkspaceTabViewModel`** (new,
  `Phantom.Workspaces/ViewModels/CloneEntityWorkspaceTabViewModel.cs`):
  - workspace-tab wrapper for `CloneEntityEditorViewModel`.
- **`CloneEntityEditorView.axaml`** (new):
  - hosted inside the clone tab (entity fields + relationship selector list + Save/Cancel).
  - Save persists clone and closes/redirects tab per UX decision; Cancel closes without writing.

### Touched: `EntityListNodeViewModel`

This already owns most edit-mode state (`IsEditMode`, `EnterEditMode`, `SaveEditMode`,
`DiscardEditMode`, `ToggleEditModeCommand`, JSON toggle, field editors). Changes:

- **Edit gating via shortcut.** `ToggleEditModeCommand.CanExecute` and
  `ShowEditIndicator` consult `entity.CanEditEntity` (via the new shortcut) instead of
  only `FieldEditors.Count > 0`, so the Edit button is enabled whenever editing is
  permitted even before field editors are lazily built.
- **Disable shortcuts/badges in edit mode.** Add `bool AreShortcutsEnabled => !IsEditMode`
  and `bool AreBadgesEnabled => !IsEditMode`; raise both from the `IsEditMode` setter.
  `EntityShortcutViewModel.IsEnabled` and the badge buttons bind to these (see XAML).
- **Keep JSON toggle live in edit mode.** `ShowJsonButton` already shows when the entity
  `CanToggleRawJson`; ensure it is not suppressed by `HasShortcuts` while `IsEditMode`,
  so the `{}` button stays available during editing.
- **Live validation.** Add `JsonValidationViewModel Validation { get; }` (see below). The
  `RawJsonText` setter (and field-editor changes that re-serialize to JSON) feed the
  current JSON into `Validation.Update(json)`. `SaveEditModeCommand.CanExecute` requires
  `Validation.IsValid`.
- **Save path.** `SaveEditMode` serializes the current editor state (JSON-mode text or
  field-editor tree) and calls `entity.SaveEditedEntityAsync(...)`.
- **Default expansion.** When building field editors, set initial `IsExpanded = true` on
  object/array/localized expanders, except localized sub-entries whose locale is not the
  current UI culture or `default`.

### New: `JsonValidationViewModel`

- New `Phantom.Workspaces/ViewModels/JsonValidationViewModel.cs`.
- Holds `bool IsValid`, `string StatusText`, `bool HasError` (for red styling).
- `void Update(string json)`:
  1. Parse with `JsonDocument.Parse`. On failure → `IsValid = false`,
     `StatusText = ex.Message`, `HasError = true`.
  2. On success, compose the schema for the entity's `entity-types` (reuse the same
     composition `SchemaValidatingDataAccessLayer` performs — extracted into a shared
     `IEntitySchemaComposer`) and call `JsonSchema.Evaluate`. First evaluation error →
     `IsValid = false`, `StatusText = <error>`, `HasError = true`.
  3. Otherwise → `IsValid = true`, `StatusText = "👍 Valid"`, `HasError = false`.
- Validation runs off the UI thread (the schema evaluate is CPU work) and marshals
  results back; updates are event-driven (no timers), consistent with the deterministic
  no-timing test convention.

### New / extracted: `IEntitySchemaComposer`

`SchemaValidatingDataAccessLayer` already composes a `Json.Schema.JsonSchema` from an
entity's `entity-types`. Extract that composition into an injectable
`IEntitySchemaComposer.ComposeAsync(IReadOnlyList<string> entityTypes)` so both the
data-access validation path and the editor's `JsonValidationViewModel` share one
implementation (no duplicated schema logic).

### Touched: `FieldEditorFactory` / `FieldTypeResolver` / `ResolvedFieldType`

- **`ResolvedFieldType.FieldEditorTypeName`** (new `string?`), populated from
  `x-field-editor` by a new `FieldTypeResolver.ReadFieldEditorTypeName` helper (sibling of
  `ReadDefaultMimeType`).
- **`FieldEditorFactory.CreateFieldEditorAsync`** checks `resolvedType.FieldEditorTypeName`
  first and, if set and resolvable, constructs the custom editor; otherwise falls through
  to the existing `switch` on `TypeName`.
- **`FieldEditorFactory.BuildFieldEditorsAsync`** gains a `bool expandAll` parameter and
  multi-entity-type union ordered by `entity-display-order` (see Data-model changes). It
  exposes the per-type ordering via a new `EntityTypeViewCatalog` lookup that also returns
  `entity-display-order`.
- A new `CustomFieldEditorActivator` encapsulates `Type.GetType` + construction so it is
  unit-testable and the fallback behavior is centralized.

### Touched: `EntityCardControl.axaml`

- Bind shortcut buttons' `IsEnabled` to the node's `AreShortcutsEnabled` (in addition to
  the per-shortcut `IsEnabled`).
- Bind the badge `ItemsControl`'s `IsEnabled` to `AreBadgesEnabled`.
- Ensure the `{}` JSON button remains visible/enabled in edit mode.
- Add a JSON validation status line (visible only in edit mode): a `TextBlock` bound to
  `Validation.StatusText`, with a style class toggled by `Validation.HasError` to render
  red. Prefer a shared style class (`workspace-validation-error`) over inline color
  (per the centralized-styling convention).
- The `>` expand-to-all-fields control reuses the existing `entity-card-expand-button`
  bound to a command that toggles `expandAll` and rebuilds the field editors.

### Touched: `MainWindowViewModel`

- Register `EditEntityShortcutHandler`.
- Register `CloneEntityShortcutHandler`.
- Provide the `IEntitySchemaComposer` (and `CustomFieldEditorActivator`) to the card-node
  / `FieldEditorFactory` construction paths.

## Field / type editor resolution order (final)

For each field, `CreateFieldEditorAsync` resolves the editor in this order:

1. `x-field-editor` on the resolved field/type schema → custom editor (assembly-qualified).
2. Built-in editor selected by `ResolvedFieldType.TypeName`
   (`local-string`, `mime-attachment`, `array`, `object`, …).
3. Default `StringFieldEditorViewModel`.

## Tests to write

### Schema / data

- `entity-type.json` accepts an entity with `entity-display-order` (number) and still
  accepts one without it (optional).
- Every `schema-definitions/*-entity-type.json` has an `entity-display-order` and all
  values are **unique** (a test that loads them all and asserts distinctness).

### EditEntityShortcut

- `EditEntityShortcutHandler.ShouldApplyTo` is `true` for `Shortcut.Edit` when
  `CanEditEntity` and `false` otherwise (deleted / non-`JsonElement` / read-only DAL).
- `ShortcutManager.GetShortcutsFor` includes `Edit` for an editable entity and excludes it
  for a non-editable one.
- `Handle` puts the corresponding card node into edit mode.

### CloneEntityShortcut

- `CloneEntityShortcutHandler.ShouldApplyTo` is `true` for `Shortcut.Clone` when
  `CanEditEntity` and source snapshot data is available.
- `ShortcutManager.GetShortcutsFor` includes `Clone` for a cloneable entity and excludes it
  for a non-cloneable one.
- `Handle` opens a new clone editor tab prepopulated with:
  - new GUID `entity-id`,
  - copied data/properties,
  - derived clone names.
- Cancel closes the tab and performs **no** update/create operations.
- Save creates the clone entity and only the selected relationships.
- Selected relationship clones rewrite source-entity participant references to the clone id.

### Edit-mode chrome (`EntityListNodeViewModel`)

- Entering edit mode sets `AreShortcutsEnabled == false` and `AreBadgesEnabled == false`.
- The JSON toggle (`ShowJsonButton`) stays available in edit mode.
- `ToggleEditModeCommand.CanExecute` reflects `CanEditEntity`.
- Save is disabled when validation is invalid; enabled when valid.
- Discard restores the pre-edit field editors and raw JSON snapshot.

### Live JSON validation (`JsonValidationViewModel`)

- Syntactically invalid JSON → `IsValid == false`, `HasError == true`, `StatusText`
  contains the parse error.
- Syntactically valid but schema-invalid (e.g., missing required `names`) → `IsValid ==
  false`, `HasError == true`, `StatusText` is the schema error.
- Valid JSON → `IsValid == true`, `HasError == false`, `StatusText == "👍 Valid"`.
- Validation updates are event-driven and deterministic (no timing waits).

### Field list rendering

- Non-JSON edit mode renders the curated (view) field set; expanded renders all fields.
- An entity with multiple entity types renders the **union** of their fields, sorted by
  the `(absolute, entity-type-name, relative, name)` tuple, de-duplicated.
- A field with `x-absolute-entity-display-order` sorts strictly by that value and appears
  before all non-absolute fields/entities.
- `x-relative-entity-display-order` orders fields within their own entity type's group;
  it defaults to `0` when absent.
- `absolute` falls back to the contributing type's `entity-display-order` when the field
  has no `x-absolute-entity-display-order`.
- Object/array/localized expanders default to expanded.
- A localized value whose locale is neither current UI culture nor `default` defaults to
  collapsed.

### x-field-editor

- A field/type schema with `x-field-editor` set to a **registered short name** (e.g.
  `entity-reference`, `markdown`) yields the corresponding well-known editor view-model
  type from `CreateFieldEditorAsync`.
- A field/type schema with `x-field-editor` set to an **assembly-qualified name** of a
  valid `EntityFieldEditorViewModel` subtype yields an instance of that type.
- Short-name matching is case-sensitive (schema/data values are matched exactly).
- The `CustomFieldEditorActivator` short-name registry maps every well-known editor (a
  completeness test).
- An unresolvable/invalid `x-field-editor` (unknown short name or unloadable type) falls
  back to the default editor for the `TypeName` (and logs).
- `ResolvedFieldType.FieldEditorTypeName` is populated from the schema node.

### Persistence

- Saving an edited entity (valid JSON) pushes a merged update through the data-access
  layer; the resulting snapshot reflects the edits.
- Saving is blocked while JSON is invalid.

## Notes

- Persistence format is unchanged; all locale/expansion/validation concerns are UI-layer.
- All data access in the save/validation paths is asynchronous (no GUI freezes,
  no `GetAwaiter().GetResult()`), and uses `ConfigureAwait(false)` in the data layer.
- Schema composition is shared between the DAL and the editor to avoid divergence.
