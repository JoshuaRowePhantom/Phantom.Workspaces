# Entity card layout

The shared header lays out the name, individual interest badges, individual
shortcuts, then auxiliary status/edit actions in forward source order. The name
is a selectable, wrapping rectangular token; actions do not reserve space ahead
of it. Type metadata occupies a separate secondary line.

`EntityCardFlowItemsPanel` preserves ItemsControl templates, commands and logical
bindings while placing their generated containers in the header's single flow.
Collection changes invalidate the shared measure and arrange.

Nested trees add 40 DIP per level. Flattened lists independently add 44 DIP per
level; these two offsets must not be combined on the same hierarchy.
`EntityTreeConnectors` uses realized immediate headers transformed into its own
coordinate space. The rail terminates at the logical last child's header when
realized, otherwise continues to the clipped extent rather than treating a
visible intermediate child as the terminus. It never forces realization.
Layout tracking is detached with the visual and drawing is noninteractive.
