# Entity status badges

## Purpose

Show an entity's **status** (task status, pull-request status, comment-thread status, etc.) as
a colored badge on its entity card. Status-bearing fields are annotated in their JSON schema
with `x-field-status`, which declares which values are "good" and "bad". A color selector turns
good values **green**, bad values **red**, and every other value into a stable, distinct,
vibrant color (never red or green) chosen by hashing the value — so equal status values always
render the same color. The entity card collects a badge for **every** status field on the
entity (across all its entity types) and renders them.

This reuses the badge row already on the card (which today shows interest badges) and the
centralized styling approach (no inline colors).

## Schema

### `field-status` definition (in `core.json`)

Add a reusable `field-status` definition to `core.json/$defs`:

```json
"field-status": {
  "description": "Annotation for a field whose value represents a status. Declares which status values are 'good' (rendered green) and which are 'bad' (rendered red). Any other value gets a stable, distinct color derived by hashing the value.",
  "type": "object",
  "properties": {
    "good-status-values": {
      "type": "array",
      "items": { "type": "string" },
      "description": "Status values rendered green (e.g. complete, closed)."
    },
    "bad-status-values": {
      "type": "array",
      "items": { "type": "string" },
      "description": "Status values rendered red (e.g. blocked, abandoned)."
    }
  },
  "unevaluatedProperties": false
}
```

### `x-field-status` annotation

A status field is annotated in its schema with `x-field-status`, a `field-status` object:

```json
"status": {
  "type": "string",
  "enum": ["pending", "in-progress", "completed", "blocked", "cancelled"],
  "x-field-status": {
    "good-status-values": ["completed"],
    "bad-status-values": ["blocked", "cancelled"]
  }
}
```

Fields to annotate initially:

- `task.json` `status` (`completed` good; `blocked`, `cancelled` bad).
- `azure-devops-pull-request.json` `status` (`completed` good; `abandoned` bad; `active`
  hashed) and `merge-status`/`build-status` as additional status fields if desired.
- `azure-devops-pull-request-comment-thread.json` `status` (`fixed`, `closed` good; `wont-fix`
  bad; `active`, `pending` hashed).
- Any future field whose value is a lifecycle/state value.

### Case sensitivity (matching rule)

Matching a field's value against `good-status-values` / `bad-status-values`, and hashing for
other values, is **case-sensitive** — both the schema lists and the entity field value are
**data**, and the repository convention is to match data case-sensitively (case-insensitive
matching is only for user-typed input). Authors therefore list the **exact** status strings the
data uses (e.g. the field's `enum` values). This avoids ambiguity and keeps colors stable.

## Color model

For a given status value:

1. If it is in `good-status-values` → the **green** status brush.
2. Else if it is in `bad-status-values` → the **red** status brush.
3. Else → one of N **other** brushes, chosen by `hash(value) mod N`. The palette excludes red
   and green hues, so a hashed value never collides visually with good/bad. Equal values map to
   equal colors (deterministic).

### Deterministic hashing

The hash must be **stable across runs, processes, and machines** (so the same status value is
always the same color). Use a deterministic content hash (e.g. FNV-1a or a truncated SHA-256
of the UTF-8 bytes) — **not** `string.GetHashCode()`, which is randomized per process. Index =
`hash mod paletteLength`.

### Palette (centralized theme resources)

Define the status palette as theme brush resources in `SharedStyles.axaml` (dark-theme
thematically chosen, vibrant but distinct, readable on the card surface):

- `Theme.Status.Good` — green.
- `Theme.Status.Bad` — red.
- `Theme.Status.Palette.0 … Theme.Status.Palette.5` — the "other" colors: **6** curated,
  distinct hues with **no red and no green** (e.g. blue, indigo, violet, teal, amber, slate).
- `Theme.Status.Foreground` — a single readable foreground for badge text (the palette is
  chosen at a saturation/luminance where one foreground reads on all of them); if needed, a
  light/dark foreground is selected per-brush by luminance.

All status colors are resources — **no inline colors** in AXAML or view models (per the
centralized-styling convention). The selector returns a **resource key**, and the badge style
binds the background to that key.

## Field resolution

Status fields are discovered the same way other field metadata is (mirroring
`x-default-mime-type` / `x-entity-types` / `x-field-editor`):

- **`ResolvedFieldType`** gains `FieldStatus? FieldStatus { get; init; }` (a record with
  `IReadOnlyCollection<string> GoodStatusValues` and `BadStatusValues`).
- **`FieldTypeResolver`** gains `ReadFieldStatus(JsonElement schemaNode)` (sibling of
  `ReadDefaultMimeType`) that parses `x-field-status` into `FieldStatus`, and populates it on
  the `ResolvedFieldType`.

## Status color selector

New `StatusColorSelector` (a small, pure, unit-testable service):

```csharp
public sealed class StatusColorSelector
{
    // Returns the theme resource KEY for a status value given its field-status annotation.
    public string SelectStatusBrushKey(string statusValue, FieldStatus? fieldStatus);
}
```

- `fieldStatus.GoodStatusValues.Contains(statusValue)` → `"Theme.Status.Good"`.
- `fieldStatus.BadStatusValues.Contains(statusValue)` → `"Theme.Status.Bad"`.
- otherwise → `$"Theme.Status.Palette.{StableHash(statusValue) % 6}"` (6 "other" colors).

Only annotated status fields are badged (see card integration), so a badge always has a
`fieldStatus`. The parameter stays nullable for callers/tests, but the card never builds a
badge for an un-annotated field.

The selector knows only keys and the palette size (6); the actual colors live in styles.

## Status badge model and card integration

### Model

A status badge is distinct from the interest `BadgeModel`. It carries **no field label** — the
badge is a pill showing only the status value, its color conveying good/bad/other, with the
field name available in the tooltip for disambiguation:

```csharp
public sealed record StatusBadgeModel(
    string StatusValue,    // the status string shown on the pill (the only visible text)
    string BrushKey,       // theme resource key from StatusColorSelector
    string Tooltip);       // optional context, e.g. "status: completed" (disambiguates fields)
```

A `StatusBadgesModel` / `StatusBadgesViewModel` holds the ordered collection (parallel to the
existing `BadgesModel` / `BadgesViewModel`).

### Building the badges

When the card view model builds (`EntityListNodeViewModel` / `ViewEntityViewModel`), it scans
the entity's fields for status annotations:

1. Enumerate the entity's fields across **all its entity types** (the same union the field list
   uses, per `entity-editor.md`), resolving each field's type via `FieldTypeResolver`.
2. For each field whose `ResolvedFieldType.FieldStatus` is present **and** whose value is a
   non-empty string, create a `StatusBadgeModel` using `StatusColorSelector` for the brush key.
3. Collect all such badges (a PR can show `status` + `merge-status` + `build-status`).

Only fields **annotated** with `x-field-status` produce status badges — arbitrary string fields
are never badged (an un-annotated status field shows no badge).

### Rendering

- Add a status-badge `ItemsControl` to `EntityCardControl.axaml` in the card's actions/badges
  row (next to interest badges), bound to `StatusBadges`.
- A centralized `Border.status-badge` style renders a rounded pill: `Background` bound to the
  badge's `BrushKey` via a `DynamicResource`/key-to-brush converter, foreground
  `Theme.Status.Foreground`, the `StatusValue` as text, and `ToolTip.Tip` = `Tooltip`.
- Status badges are display-only (not interactive) and remain visible in read mode; in edit
  mode they may dim with the rest of the non-edit chrome (consistent with the entity-editor
  design).

A small `StatusBrushKeyConverter` (or a styled `ContentControl` keyed on `BrushKey`) resolves
the resource key to the actual brush at render time, keeping all colors in styles.

## Source layout / code changes

In `Phantom.Workspaces.Data.Core`:

- `JsonSchemas/core.json` — add the `field-status` `$def`.
- Annotate status fields with `x-field-status` (`task.json`, `azure-devops-*` schemas, …).
- `FieldTypeResolver.cs` / `ResolvedFieldType.cs` — `ReadFieldStatus` + `FieldStatus`.

In `Phantom.Workspaces` (GUI):

- `StatusColorSelector.cs` — the selector (pure logic).
- `StatusBadgeModel.cs` / `StatusBadgesViewModel.cs` — the badge model/view model.
- `EntityListNodeViewModel` / `ViewEntityViewModel` — build `StatusBadges` from the entity's
  annotated fields.
- `Controls/EntityCardControl.axaml` — the status-badge `ItemsControl`.
- A `StatusBrushKeyConverter` (or keyed style) to resolve a brush key to a brush.

In `Phantom.Workspaces.Gui.Styles`:

- `SharedStyles.axaml` — `Theme.Status.Good`, `Theme.Status.Bad`,
  `Theme.Status.Palette.0..N-1`, `Theme.Status.Foreground`, and the `Border.status-badge`
  style.

## Tests to write

Schema/data:

- `core.json` exposes `field-status`; a schema using `x-field-status` validates, and the
  annotation shape (`good-status-values` / `bad-status-values` arrays) is accepted.
- `FieldTypeResolver.ReadFieldStatus` populates `ResolvedFieldType.FieldStatus` from
  `x-field-status` (and leaves it null when absent).

Color selector (pure, deterministic):

- A value in `good-status-values` → `Theme.Status.Good`; in `bad-status-values` →
  `Theme.Status.Bad`.
- An "other" value → a `Theme.Status.Palette.{i}` key; the **same** value always yields the
  **same** key (determinism across runs — assert with a fixed expected index for a known
  input, proving the hash is stable, not `GetHashCode`-based).
- Two different "other" values generally map to different palette keys (collision only by
  modulo); no "other" value ever maps to good/bad keys.
- Matching is **case-sensitive**: `"Completed"` is not treated as `"completed"` unless both are
  listed exactly.

Badge building:

- An entity with an annotated `status` field produces one `StatusBadgeModel` with the correct
  value, brush key, and tooltip.
- A PR-like entity with multiple annotated status fields produces a badge per field.
- A field without `x-field-status` produces no status badge (default); an empty/missing status
  value produces none.
- Status fields from **all** of a multi-type entity's types are included.

Rendering (view-model level, deterministic — no pixel tests):

- The card exposes the expected `StatusBadges` collection; the `StatusBrushKeyConverter`
  resolves a known key to the configured brush resource.

Determinism: pure logic + in-memory schema/entities; no timing or rendering-thread waits.

## Resolved decisions

1. **No field label on the badge.** A status badge is a pill showing only the status value
   text; the pill **background color** conveys good/bad/other. The field name is not shown on
   the badge (redundant) but is available in the tooltip for disambiguation when an entity has
   multiple status fields.
2. **Palette size = 6.** Six curated, distinct "other" hues (no red/green):
   `Theme.Status.Palette.0..5`.
3. **Annotation-driven only.** The card never badges a string field that lacks
   `x-field-status`.
4. **Good/bad hues are theme resources.** "Thematically chosen" green/red are defined in
   `SharedStyles.axaml` (`Theme.Status.Good` / `Theme.Status.Bad`), not raw
   `#00FF00`/`#FF0000`.