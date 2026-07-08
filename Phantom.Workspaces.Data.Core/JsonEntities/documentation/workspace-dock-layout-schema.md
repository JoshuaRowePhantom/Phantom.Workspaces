# Workspace dock layout schema

JSON schema for the `dock-layout` property of workspace entities. Stores a
`Dock.Serializer.SystemTextJson` snapshot of the workspace content layout — splits,
proportions, active-tab-per-dock, and per-tab `Descriptor` objects needed to
recreate tabs after restart.

A missing or structurally invalid value is silently ignored; the workspace falls
back to default single-pane layout.
