# Interests and Badges Model

## Purpose

Interests express contextual relevance (for users, views, or globally) and are rendered as badges on entities.

## Badge model

Badges should support both text and styling for applied/not-applied states.

```csharp
public enum BadgeApplicationState
{
    NotApplied,
    AppliedToCurrentView,
    AppliedToCurrentUser,
    AppliedToCurrentUserAndView,
    Applied
}

public sealed record BadgeModel(
    string Text,
    IReadOnlyList<string> AppliedClasses,
    IReadOnlyList<string> NotAppliedClasses,
    BadgeApplicationState State);
```

### State meaning

- `NotApplied`: interest exists but does not apply in the current context.
- `AppliedToCurrentView`: interest applies due to current view-scoped relationship.
- `AppliedToCurrentUser`: interest applies due to current user-scoped relationship.
- `AppliedToCurrentUserAndView`: interest applies to both current user and current view scopes.
- `Applied`: interest applies globally (not tied to user/view scope).

## Interest typing and relationship rules

Interest behavior is driven by:

1. Entity type `interests` configuration (which interest types are allowed for the entity type).
2. Interest type `entity-types` configuration (which participant entity types are valid for that interest type relationship).

## Scope semantics

1. If an interest type allows a `view` participant, that interest can be applied within a view scope.
2. If an interest type allows a `user` participant, that interest can be applied within a user scope.
3. If an interest type allows both `user` and `view`, both scopes may simultaneously apply.
4. If an applied interest has neither `user` nor `view` participants, it is treated as globally applied (`Applied`).

## Notes for implementation

- `SubscribedEntityViewModel` should own a badges model instance.
- `BadgesViewModel` should project badge presentation and state for binding.
- Badge class assignment should be state-driven (`AppliedClasses` vs `NotAppliedClasses`).
- Final badge computation should be relationship-derived, not from ad-hoc payload fields.

## Missing information / decisions needed before implementing interest types

> Context: the todos `actionable-blocked-interest-types`, `classifier-interest-instructions`,
> `interests-toggleable-glyphs`, `not-interesting-filter`, and `inbox-actionable-view` all need new
> interest **types** — `actionable` (by user), `blocked` (by user), `assigned-to` (by user), and
> `not-interesting`. Today the model is only partially built (`BadgesModel`/`BadgeModel` exist but
> `SubscribedEntityViewModel.Badges` is never populated; **no** interest-type entities are seeded;
> entity types have **no** `interests` configuration; there is no interest/participant query clause).
> The following must be decided/specified before implementation:

1. **Interest type vs. instance shape.** Confirm the split between an interest **type** (a seeded
   definition carrying the `applied`/`notApplied` UX text and the allowed participants) and an
   interest **instance** (the relationship linking a target entity to a user/view). `interest.json`
   currently puts `applied`/`notApplied` *and* `participants` on one entity (see `interest-schema.md`),
   which conflates the two. Specify: does each interest type get one type entity (named
   `["interests", "<name>"]`) plus per-application instances, and how does an instance reference its
   type?

Each interest type gets an interest-type entity type derived from relationship-type entity type
to represent the class of interests, for example an "actionable" interest-type entity
will be a relationship-type entity declaring participant roles for "target" (the entity carrying the interest), 
"user" (the user for whom it's actionable), and optionally "view" (the view for which it's actionable). 
An application of the "actionable" interest is an entity of entity type "actionable"
whose participants are the target entity, the user for whom it's actionable, and optionally the view if it's actionable in a view scope.

2. **"By a user" participant model.** §"Interest typing" says an interest type's `entity-types`
   configuration declares valid participant entity types, but `interest.json` has no such field
   (its `entity-types` is the entity's own type array). Specify how a type declares it allows a
   `user` (and/or `view`) participant, and the **participant role names** on an instance (e.g.
   `participants.target` = the entity, `participants.user` = the user for "actionable by user").

"interest" relationship types may have a user participant.

3. **Entity-type `interests` configuration.** §"Interest typing" says each entity type declares which
   interest types it allows, but `entity-type.json` has no `interests` field. Specify its shape and
   which entity types allow `actionable`/`blocked`/`not-interesting` (per the workstreams decision,
   each entity type's classifier instructions define its transitions, implying broad applicability).

Fix as part of this design. The "interest" entity type perhaps should be derived from entity-type and relationship-type
and renamed "interest-type").
relationship-type.json says each participant property must have an x-entity-type, though that should be
changed to x-entity-types and be an optional array of entity types, where empty array means "no entity types"
and no array means all entity types.

4. **Seeding location & identity.** Where do interest-type entities live (e.g.
   `JsonEntities/interests/*.json`), what `entity-types` do they carry (`interest` + `entity-type`?
   `interest` + `relationship-type`?), and what are their names? `SchemaPopulatorTests` (all-or-nothing
   populate) will gate their validity.

Interest types are in entity-types. 

5. **Badge computation pipeline.** `SubscribedEntityViewModel.Badges` is currently never populated.
   Specify how interest instances (via the entity's relationships) are mapped to `BadgeModel`s with
   applied/not-applied state for the current user/view context. Reconcile the richer model in this doc
   (`BadgeApplicationState`, `applied`/`notApplied` indicator/description/actionText) with the current
   `BadgeModel(InterestTypeEntityType, Label, IsActive)`.

Design this as part of "View" view model / entity view model class design.

6. **Query support (for inbox & workstreams).** A query clause is needed to select entities carrying
   interest `X` whose participant `user` is the current user (e.g. inbox = all entities with
   `actionable` for the current user; workstreams = tasks with `assigned-to` for the current user).
   Specify the clause (interest/participant match) and how the current user is bound (see the
   query-DAL session-context work in `docs/design/workstreams-view.md`).

This is true. Design this.

7. **Is `assigned-to` an interest?** The workstreams design treats assignment as an `assigned-to`
   (by-user) interest derived by the classifier from the source-system `assigned-to` field. Confirm it
   is modeled as a by-user interest alongside `actionable`/`blocked` (and therefore part of this work),
   or as a distinct relationship.

Yes, this is an interest.

The workspaces view model should maintain a subscribed query to retrieve all entity
types, which will include interest types. This query should be used to drive the badge model projection for each entity, based on the relationships returned in the entity queries. 
The badge state should be computed from the presence of relationships of the relevant interest types, and their participant scopes (user/view).


## more

Each relationship applied by the AI workspace tools should be required to carry a note for -why- the interest or relationship
was applied. Entity cards should show a mouseover for the relationships presented on the view to explore the reasons
why; users should be to edit the reason, too. This is enforced in the AI workspace tooling via 
a required `note` property on the relationships it creates, and surfaced in the UI via a tooltip on the badge that shows the note content. 
This helps build user trust and understanding of the AI's actions.

## Decided design (v1)

This section consolidates the review answers above into an implementable spec.

### Type layering

- **`interest-type`** is a meta entity type (analogous to `relationship-type`), derived from **both**
  `entity-type` and `relationship-type`. It replaces/renames the current `interest` entity type.
- Each concrete interest — `actionable`, `blocked`, `assigned-to`, `not-interesting` — is an
  **`interest-type` definition entity** (like `tool-relationship-entity-type.json`): `entity-types`
  `["interest-type", "relationship-type", "entity-type", "note"]`, named
  `["entity-types", "<interest>"]` + a `["json-schemas", ".../<interest>.json"]` name, with a JSON
  schema (`allOf $ref relationship.json`) declaring its participant roles.
- **Participant roles** for a by-user interest: `target` (the entity carrying the interest, required),
  `user` (the user for whom it applies — required for by-user interests), and optional `view` (when
  the interest is view-scoped). `assigned-to` is modelled exactly this way (target = task, user =
  assignee).
- An **application** of an interest is a relationship entity of that interest type whose `participants`
  are the target entity, the user, and optionally the view. Badge state derives from the presence of
  such relationships and their participant scopes (user/view) — see Scope semantics.

### `x-entity-types` participant schema change

`relationship-type.json` currently requires each participant property to carry a single
`x-entity-type`. Change the convention to **`x-entity-types`**: an **optional array** of entity-type
ids, where an **empty array means no entity types are allowed** and an **absent** keyword means **all
entity types are allowed**. The referential-integrity layer already accepts string-or-array under
`x-entity-type`; implement `x-entity-types` additively (accept both; prefer `x-entity-types`),
preserving the empty-vs-absent distinction, with tests. Migrate existing participant schemas to
`x-entity-types`.

### Seeding

Interest types live with the other entity-type definitions under
`Phantom.Workspaces.Data.Core/JsonEntities/schema-definitions/` (validated all-or-nothing by
`SchemaPopulatorTests`), with flat `JsonSchemas/<interest>.json` schemas and documentation notes.

### Badge computation pipeline

The workspaces view-model maintains a **subscribed query for all entity types** (which includes the
interest types). For each displayed entity, its returned relationships are projected into the badge
model: a badge per interest type, with applied/not-applied state computed from the presence of
relationships of that interest type and their participant scopes (current user / current view) per
the Scope semantics above. Reconcile `BadgeModel(InterestTypeEntityType, Label, IsActive)` with the
richer `applied`/`notApplied` indicator/description/actionText and `BadgeApplicationState` as part of
the View / entity view-model class design. Badge tooltips show the relationship's `note` (reason).

### Interest/participant query clause

Add a query clause to select entities carrying interest `X` whose `user` participant is a given user
(bound from the session context per `docs/design/workstreams-view.md`). Inbox = all entities with
`actionable` for the current user; workstreams = tasks with `assigned-to` for the current user.

### Relationship reason-note enforcement

AI workspace tools must require a **`note` (reason) property** on every relationship they create
(this is a property capturing *why*, not a requirement that the relationship be entity-type `note`).
Enforced in the workspace entity update tooling; surfaced as an editable badge/relationship tooltip.

### Build order

`actionable-blocked-interest-types` (the `interest-type` model + `x-entity-types` change + seeded
interest types) is the foundation; `classifier-interest-instructions`, `interests-toggleable-glyphs`
(badge pipeline), `not-interesting-filter`, `inbox-actionable-view`, and the workstreams query depend
on it.

