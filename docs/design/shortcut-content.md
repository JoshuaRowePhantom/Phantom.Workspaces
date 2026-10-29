# Shortcut content

`Shortcut.Content` is optional, immutable presentation metadata. Omission keeps
the existing selectable/copyable `Label`. `Name` and `Label` alone still define
equality and dispatch; the semantic `HoverText` supplies tooltip and automation
name independently of the visible content.

Supported descriptors:

- `ShortcutTextContent`: literal text, at most 128 characters.
- `ShortcutImageContent`: a `ShortcutImageResource` enum value, never a path/URI.
- `ShortcutCompositeContent`: a defensively copied, read-only sequence of one to
  four text/image leaves. No recursive compositions.

`ShortcutContentControl` registers trusted `FuncDataTemplate` implementations
for these types. Each builds fresh controls; only immutable packaged bitmaps
are shared. `ShortcutButtonContentTemplate` is registered by `App.axaml` and
used by the shared entity-card shortcut button, so tree, flattened, nested,
single-entity and floating-window card hosts retain the same renderer. Other
copyable action templates are unchanged.

To extend the catalog, add an enum value and a fixed packaged-resource mapping
with provenance/permission review and tests. Do not accept entity-defined XAML,
HTML, scripts, control types, filesystem paths, remote URLs, or recursive UI
trees. Unknown descriptors/resources or resource loading failures produce a
trace warning and readable semantic fallback without disabling the action.
There is no serialized presentation schema, persisted migration or live entity
edit in this change.

Text-only content forwards selection/copy and activation to the existing outer
button. Compositions are decorative and noninteractive; their outer button
alone owns keyboard/pointer activation and accessibility. The fixed 28x28
target and shared focus styling are unchanged. The VS Code mark uses 16x16
with four-pixel clear space; in the web composition it uses 10x10 with 2.5-pixel
clear space beside a separate globe, preserving aspect ratio and original
colors on both themes. No distinct licensed web logo is claimed.

See `Phantom.Workspaces/Assets/Shortcuts/NOTICE.txt` for the official asset source,
checksum, permitted action-identification use and Microsoft trademark notice.
The notice is copied to build/publish output alongside the packaged artwork.
