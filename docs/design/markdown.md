# Markdown in Phantom.Workspaces

## Purpose

Define one markdown rendering approach for Phantom.Workspaces so every markdown surface behaves
consistently (rendering, editing, styling, and safety).

## Current state

The workspace application renders markdown with the free, MIT-licensed **`Markdown.Avalonia`**
renderer (`Markdown.Avalonia.Tight` + `Markdown.Avalonia.SyntaxHigh`, pinned to `12.0.0-a3`, the only
build compatible with the repository's Avalonia `12.1.0`):

1. the packages are referenced from `Phantom.Workspaces.Gui.Shared.csproj` with
   `PrivateAssets=compile` (see the licensing/namespace note below).
2. the markdown, `AvaloniaEdit`, and syntax-highlight themes are loaded in
   `Phantom.Workspaces.Gui.Shared/Styles/SharedStyles.axaml`.
3. markdown fields are rendered in `Phantom.Workspaces/Templates/WorkspaceDataTemplates.axaml`
   with the shared `WorkspaceMarkdownView` control.

Current markdown surfaces include:

1. generic entity display markdown (`EntityDisplayItemViewModel` template),
2. markdown mime attachments (`MarkdownMimeAttachmentFieldEditorViewModel`),
3. json schema read rendering (`JsonSchemaFieldEditorViewModel.MarkdownText`).

The agent chat user interface currently does not use a markdown control; it renders chat output as
selectable text and flow document blocks.

## Decision

Use **one markdown control family everywhere**: the free, MIT-licensed `Markdown.Avalonia` renderer
(`Markdown.Avalonia.Tight` + `Markdown.Avalonia.SyntaxHigh`, exposed through the shared
`WorkspaceMarkdownView` control).

Do **not** use the commercial `Avalonia.Controls.Markdown`/`Avalonia.Controls.RichTextEditor`
controls: without an Avalonia Accelerate license they raise `AVLIC0001` at build time. The free
`Markdown.Avalonia` packages carry no commercial (AVLIC) dependencies.

Do not introduce parallel markdown engines (for example Markdig-driven custom controls, web view
markdown renderers, or ad hoc converters) for normal in-application markdown.

### Namespace and licensing isolation

`Markdown.Avalonia.dll` declares a root `Markdown` namespace that collides with the pervasive domain
type `Markdown` (used as `new Markdown { Text = ... }`) and with Markdig's `Markdown.Parse` in
`Phantom.Workspaces.Agent.Gui`. Assembly aliases do not suppress this collision. To keep the renderer
confined:

1. `WorkspaceMarkdownView` **composes** (hosts) a `Markdown.Avalonia.MarkdownScrollViewer` internally
   rather than inheriting from it, so consuming projects reference only the shared control.
2. the `Markdown.Avalonia` references use `PrivateAssets=compile`: compile assets stay private to
   `Phantom.Workspaces.Gui.Shared` (no namespace leak into other projects) while runtime assets still
   flow so the application ships the renderer DLLs.

## Single-control architecture

To enforce one implementation across the application, a shared wrapper control is used:

`WorkspaceMarkdownView` (in `Phantom.Workspaces.Gui.Shared/Controls`).

The wrapper:

1. internally hosts `Markdown.Avalonia.MarkdownScrollViewer` (with `SelectionEnabled` and the
   `SyntaxHighlight` plugin, which also renders fenced-code language labels),
2. exposes the common inputs used by the application (a `Markdown` string property, `SelectionEnabled`,
   style classes),
3. applies shared styling and safe defaults centrally,
4. is the only markdown control used by templates and views in this repository.

This keeps all markdown behavior in one place even if the underlying renderer changes.

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

In `Phantom.Workspaces.Gui.Shared`:

1. `Controls/WorkspaceMarkdownView.cs` (a `Decorator` hosting `MarkdownScrollViewer`)
2. `Styles/SharedStyles.axaml` (markdown/`AvaloniaEdit`/syntax-highlight theme includes and
   `workspace-markdown-viewer`/`workspace-markdown-editor` styles)

In `Phantom.Workspaces`:

1. `Templates/WorkspaceDataTemplates.axaml`
   - all markdown surfaces use `controls:WorkspaceMarkdownView`.
2. any other direct markdown control usage discovered later
   - migrate to `WorkspaceMarkdownView`.

In shared styles:

1. markdown-specific visual behavior lives in centralized styles for `WorkspaceMarkdownView`.

## Migration plan

1. add `WorkspaceMarkdownView` as a wrapper over the free `Markdown.Avalonia` renderer.
2. migrate all existing direct markdown usages in workspace templates to the wrapper.
3. add a repository check (test or analyzer-style guard) that flags new direct markdown-control
   references outside the wrapper implementation.
4. update design and architecture docs when new markdown surfaces are added.

## Test tasks

1. wrapper control tests:
   - markdown binding renders markdown correctly (bold/italic, headings, links, fenced code),
   - wrapper applies shared style classes,
   - wrapper uses the `Markdown.Avalonia` renderer internally.
2. template migration tests:
   - workspace data templates render expected markdown content through the wrapper.
3. behavior tests:
   - markdown attachment read and edit experiences remain unchanged after migration.

## Non-goals

1. converting agent chat output into markdown rendering.
2. introducing a second markdown rendering engine.
3. replacing markdown content storage format.
4. taking any dependency on the commercial `Avalonia.Controls.Markdown` control.

