# Tool Relationship Schema

A relationship entity that links a tool participant, schedule participants, and target participants.

## participants

### tool-participant (entity-id)
The tool entity that should execute.

### schedule-participants (entity-id[])
One or more schedule entities that drive execution timing.

### target-participants (entity-id[])
One or more target entities the tool should run against.
