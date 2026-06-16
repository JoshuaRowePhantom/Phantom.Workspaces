# Assigned-to interest schema

Marks a `target` task as **assigned to** a `user`. The entity classifier derives this interest from
a task's source-system `assigned-to` field; the workstreams view selects a user's tasks by this
interest. Derived from `relationship.json`.

## Participants

- `target` (required): the assigned task (entity type `task`).
- `user` (required): the user the task is assigned to.
