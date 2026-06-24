# Markdown in Phantom.Workspaces

## Purpose

Define one markdown rendering approach for Phantom.Workspaces so every markdown surface behaves
consistently (rendering, editing, styling, and safety).

## Current state

The workspace application already uses the official Avalonia markdown package:

1. `Avalonia.Controls.Markdown` is referenced in `Phantom.Workspaces.csproj`.
2. the markdown theme is loaded in `Phantom.Workspaces/App.axaml`.
3. markdown fields are rendered in `Phantom.Workspaces/Templates/WorkspaceDataTemplates.axaml`
   with `md:Markdown`.

Current markdown surfaces include:

1. generic entity display markdown (`EntityDisplayItemViewModel` template),
2. markdown mime attachments (`MarkdownMimeAttachmentFieldEditorViewModel`),
3. json schema read rendering (`JsonSchemaFieldEditorViewModel.MarkdownText`).

The agent chat user interface currently does not use a markdown control; it renders chat output as
selectable text and flow document blocks.

## Decision

Use **one markdown control family everywhere**: the official Avalonia control
(`Avalonia.Controls.Markdown`).

Do not introduce parallel markdown engines (for example `Markdown.Avalonia`, Markdig-driven custom
controls, web view markdown renderers, or ad hoc converters) for normal in-application markdown.

## Single-control architecture

To enforce one implementation across the application, add a shared wrapper control:

`WorkspaceMarkdownView` (name can be adjusted to project naming conventions).

The wrapper:

1. internally hosts the official `md:Markdown` control,
2. exposes the common inputs used by the application (`Text`, edit-mode binding behavior, classes),
3. applies shared styling and safe defaults centrally,
4. is the only markdown control used by templates and views in this repository.

This keeps all markdown behavior in one place even if the underlying official control changes.

## Rendering and editing model

1. **Read mode**
   - render markdown text with `WorkspaceMarkdownView`.
2. **Edit mode**
   - keep markdown source editable through the existing field editors.
   - when live preview is needed, use `WorkspaceMarkdownView` for preview rendering rather than a
     different markdown control.
3. **Styling**
   - markdown styles are centralized in shared styles and theme resources.
4. **Safety**
   - disable or constrain risky markdown features consistently in one place (for example external
     navigation behavior), using wrapper-level policy.

## Source layout and touched code

In `Phantom.Workspaces.Gui.Shared` (or the shared controls assembly used by the workspace
application):

1. `Controls/WorkspaceMarkdownView.axaml` (new)
2. `Controls/WorkspaceMarkdownView.axaml.cs` (new)
3. optional helper policy classes for markdown link and asset handling (new)

In `Phantom.Workspaces`:

1. `Templates/WorkspaceDataTemplates.axaml`
   - replace direct `md:Markdown` usage with `WorkspaceMarkdownView`.
2. any other direct markdown control usage discovered later
   - migrate to `WorkspaceMarkdownView`.

In shared styles:

1. move markdown-specific visual behavior into centralized styles for `WorkspaceMarkdownView`.

## Migration plan

1. add `WorkspaceMarkdownView` as a thin wrapper over official `md:Markdown`.
2. migrate all existing direct `md:Markdown` usages in workspace templates to the wrapper.
3. add a repository check (test or analyzer-style guard) that flags new direct `md:Markdown`
   references outside the wrapper implementation.
4. update design and architecture docs when new markdown surfaces are added.

## Test tasks

1. wrapper control tests:
   - text binding renders markdown correctly,
   - wrapper applies shared style classes,
   - wrapper uses the official markdown control internally.
2. template migration tests:
   - workspace data templates render expected markdown content through the wrapper.
3. consistency guard tests:
   - no direct `md:Markdown` usage exists outside approved wrapper locations.
4. behavior tests:
   - markdown attachment read and edit experiences remain unchanged after migration.

## Non-goals

1. converting agent chat output into markdown rendering.
2. introducing a second markdown rendering engine.
3. replacing markdown content storage format.

