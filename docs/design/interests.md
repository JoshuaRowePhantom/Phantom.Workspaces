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
