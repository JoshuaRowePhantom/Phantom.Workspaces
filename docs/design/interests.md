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

2. **"By a user" participant model.** §"Interest typing" says an interest type's `entity-types`
   configuration declares valid participant entity types, but `interest.json` has no such field
   (its `entity-types` is the entity's own type array). Specify how a type declares it allows a
   `user` (and/or `view`) participant, and the **participant role names** on an instance (e.g.
   `participants.target` = the entity, `participants.user` = the user for "actionable by user").

3. **Entity-type `interests` configuration.** §"Interest typing" says each entity type declares which
   interest types it allows, but `entity-type.json` has no `interests` field. Specify its shape and
   which entity types allow `actionable`/`blocked`/`not-interesting` (per the workstreams decision,
   each entity type's classifier instructions define its transitions, implying broad applicability).

4. **Seeding location & identity.** Where do interest-type entities live (e.g.
   `JsonEntities/interests/*.json`), what `entity-types` do they carry (`interest` + `entity-type`?
   `interest` + `relationship-type`?), and what are their names? `SchemaPopulatorTests` (all-or-nothing
   populate) will gate their validity.

5. **Badge computation pipeline.** `SubscribedEntityViewModel.Badges` is currently never populated.
   Specify how interest instances (via the entity's relationships) are mapped to `BadgeModel`s with
   applied/not-applied state for the current user/view context. Reconcile the richer model in this doc
   (`BadgeApplicationState`, `applied`/`notApplied` indicator/description/actionText) with the current
   `BadgeModel(InterestTypeEntityType, Label, IsActive)`.

6. **Query support (for inbox & workstreams).** A query clause is needed to select entities carrying
   interest `X` whose participant `user` is the current user (e.g. inbox = all entities with
   `actionable` for the current user; workstreams = tasks with `assigned-to` for the current user).
   Specify the clause (interest/participant match) and how the current user is bound (see the
   query-DAL session-context work in `docs/design/workstreams-view.md`).

7. **Is `assigned-to` an interest?** The workstreams design treats assignment as an `assigned-to`
   (by-user) interest derived by the classifier from the source-system `assigned-to` field. Confirm it
   is modeled as a by-user interest alongside `actionable`/`blocked` (and therefore part of this work),
   or as a distinct relationship.

The workspaces view model should maintain a subscribed query to retrieve all entity
types, which will include interest types. This query should be used to drive the badge model projection for each entity, based on the relationships returned in the entity queries. 
The badge state should be computed from the presence of relationships of the relevant interest types, and their participant scopes (user/view).
