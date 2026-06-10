# Schedule Schema

A schedule entity that captures when a workspace tool should run.

## Properties

### repeat (object)
Repeat cadence for the schedule.

#### repeat.frequency (time)
`core.json#/$defs/time` (`format: "time"`).

#### repeat.days-of-week (string[])
Optional day-of-week filters. Use lowercase day names.

#### repeat.start-at (time[])
One or more `core.json#/$defs/time` values.

## Inherited from entity.json
- entity-id
- entity-types
- names
- display-name
- content
