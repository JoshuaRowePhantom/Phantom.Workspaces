# Interest type schema

Defines an **interest type**: a relationship type that also carries the badge UX
(`applied` / `notApplied`) shown for the interest. Concrete interest types (for example
`actionable`, `blocked`, `assigned-to`, and `not-interesting`) derive from this and declare their
participant roles.

## Model

- An interest type is a `relationship-type` (it declares participant roles via its schema) that is
  additionally required to carry `applied` and `notApplied` badge content.
- Participant roles, by convention: `target` (the entity carrying the interest, required), `user`
  (the user for whom it applies — present for user-scoped interests), and optional `view` (for
  view-scoped interests). When neither `user` nor `view` is present on an application, the interest
  is global.
- An **application** of an interest is a relationship entity whose `entity-types` include the concrete
  interest type's name, with the corresponding participants.

## Required fields

- `applied` and `notApplied`, each with `indicator`, `description`, and `actionText` (localizable
  strings) describing the badge in that state.
