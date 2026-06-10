# Schedule entities design

Global schedule entities are named under:

- `["schedule", "<name>"]`

Global tool entities are expected under:

- `["tools", "<name>"]`

The `schedule` schema models:

- `repeat.frequency` (JSON Schema `format: "time"`)
- `repeat.days-of-week` (array)
- `repeat.start-at` (array of JSON Schema `format: "time"` strings)

The `tool-relationship` schema models:

- one `tool-participant`
- one or more `schedule-participants`
- one or more `target-participants`

These entities provide the baseline metadata needed to select tool runs and determine execution targets.
