# Phantom.Workspaces Architecture Design

## Overview

Phantom.Workspaces is a WPF-based application for running LLM agents and accessing user data.  
The system separates UI, agent execution, data access, and external integrations into dedicated projects and runtime boundaries.

## Core Runtime Design

1. **Agent execution isolation**
   - Agents run in a dedicated in-process host component focused on agent lifecycle and orchestration.
   - Conversation/session information for agent runs is persisted through the client data access layer.
2. **MCP tooling in containers**
   - MCP tools run in containers.
   - Filesystem mount points are configured at runtime.
   - Processes execute inside containers against mounted host filesystems.
3. **Sandbox boundary**
   - The sandbox is the security boundary for running host-provided executables and tools.
   - Only explicitly granted mounts, devices, and capabilities are exposed to sandboxed processes.
   - The sandboxed process runs inside the sandbox; host files are only visible through approved mounts.

## Planned Project Structure

1. **WPF application**
   - Desktop UI, user flows, and local orchestration entrypoint.
2. **MCP server**
   - Exposes MCP-compatible tool endpoints and coordinates tool execution.
3. **Client data access layer**
   - Multi-assembly design:
     - `Phantom.Workspaces.Dal.Core`
       - merging, security, and shared DAL behaviors
     - `Phantom.Workspaces.Dal.Offline`
       - `InMemory`, filesystem, and git-backed DALs
     - `Phantom.Workspaces.Dal.CosmosDB`
       - Cosmos DB DAL
     - `Phantom.Workspaces.Dal.Sql`
       - SQL DAL
     - `Phantom.Workspaces.Dal.Web.Client`
       - web client DAL
     - `Phantom.Workspaces.Dal.Web.Server`
       - web server DAL/API surface as a class library
     - `Phantom.Workspaces.Web.Server`
       - web server host and web GUI hosting entrypoint
   - The web server is expected to be hosted alongside or within the web version of the GUI.
4. **LLM agent host**
   - Dedicated project for agent hosting, orchestration, and execution policy.
5. **Web version of the GUI**
   - Planned web offering that hosts the web server and a browser-based GUI.
6. **External data integration layer**
   - Shared foundation for external-system integration services.
7. **Individual data integration tools**
   - Tool-specific projects/adapters for concrete systems.

## Multi-User Security Model

1. The system is multi-user with both shared and user-isolated data domains.
2. Every client data access layer call must:
   - authenticate the calling user identity, and
   - authorize the requested operation against resource-level access rules.
3. Data access implementations (`InMemory`, `Web`, `CosmosDB`, SQL) must enforce a consistent authorization contract so behavior is uniform regardless of backend.
4. Planned DAL backends include:
   - `InMemory`
   - `Web`
   - `CosmosDB`
   - SQL
   - filesystem-based storage
   - git-based storage built on top of filesystem storage

## Data Access Layer Contract

1. DAL operations are designed around complex request/response objects rather than many individual parameters.
2. When the API surface changes, request/response shapes should evolve by adding struct/class members where possible instead of widening method signatures.
3. `Update`:
   - applies adds, updates, and removals of entities, including relationships,
   - runs as a single transaction,
   - applies optimistic concurrency checks to each entity involved,
   - supports incremental merge-style updates against existing entity data,
   - explicitly uses `null` to remove items; schemas never permit `null`,
   - accepts a series of changes for complex updates and validates the effective entity data only at the end of the update,
   - `{"$replace": true}` on an entity or subobject disables merge behavior for that node,
   - if a field is specified more than once with a `null` value and then a concrete value, the field is treated as non-merged for that update path,
   - object merge semantics:
     - merge fields into the existing object,
     - remove fields when set to `null`,
     - merge nested objects recursively,
   - array merge semantics:
     - append by default,
     - support `{"$insert-at": index, ...data...}` to insert,
     - support `{"$remove-at": index}` to remove,
     - setting an array field to `null` clears it at the object level,
   - string merge semantics:
     - support `{"field": {"+=": "value to append"}}` to append to an existing string,
   - returns per-entity results including:
     - the new concurrency tag,
     - whether the concurrency tag matched,
     - the resulting object id when it differs from the supplied id.
4. Duplicate relationships are coalesced during `Update` and return the GUID of the existing relationship.
5. `Get`:
   - is the point-get API,
   - given a set of entities, retrieves them,
   - accepts a set of timestamps and returns results for each timestamp,
   - `now` is the latest timestamp when requested.
6. `Query`:
   - performs a recursive clause-based query,
   - accepts a set of timestamps and returns results for each timestamp,
   - `now` is the latest timestamp when requested.
   - query input consists only of top-level clauses.
   - each top-level clause may include an optional clause identifier and contains a single recursive clause.
   - clauses are recursive and may be:
     - `and` over clauses,
     - `or` over clauses,
     - `not` over a clause,
     - `top(n)` over a clause,
     - an entity query,
     - a transit query.
   - entity queries may be:
     - field queries with equals, greater-than, less-than, greater-than-or-equal-to, less-than-or-equal-to, regular-expression match, or array contains,
     - full-text queries for approximate matching with a required identifier and optional minimum score threshold,
     - participation queries by relationship type name and optional participation role name.
   - participation queries may include a must-have requirement with an optional role name and a clause that target objects must also satisfy.
   - transit queries:
     - specify a relationship type name,
     - reference a source top-level clause identifier,
     - optionally constrain source and destination participation role names,
     - match the destination entities with a clause,
     - begin from the entities produced by the preceding entity query and traverse related entities.
   - each returned entity must indicate which clauses caused it to be returned.
   - each full-text query must include an identifier.
   - `top(n)` is a separate clause that limits the results of the nested clause.
   - full-text queries may specify a minimum score threshold.
   - entities that match a full-text query after other filtering must include a score associated with the full-text query identifier.
7. `Render`:
   - renders a view into the set of entities needed to satisfy that view,
   - when provided with a time index, returns the entities modified for the view since the previous render and a new time index,
   - supports incremental display updates.
8. `GetHistory`:
   - returns the update timestamps for a set of entities,
   - supports history-aware access to entity versions.
9. `Export`:
   - returns all entities that have changed since an optional snapshot time,
   - returns the entities as of each change,
   - returns a final snapshot time.
10. `GetChangedEntities`:
   - accepts a set of entity-id/timestamp pairs,
   - returns only entities with changes later than the provided timestamp.
11. `Update` must succeed or fail as a single transaction.
12. The merge/update facility is implemented in a DAL that performs `Get` / merge / `Update` behavior on top of a sub-DAL.
13. Filesystem DAL does not support timestamped `Get` / `Query` history semantics and ignores the timestamp parameter.
14. Git DAL and other timestamp-aware DALs support the timestamp parameter for `Get` / `Query`.

## Filesystem and Git Data Access

1. The filesystem DAL stores each entity in a file named for its GUID.
2. To reduce filesystem lookup cost, entity files are sharded into three directory levels using the first three bytes of the GUID.
3. The filename still contains the full GUID.
4. The git-based DAL is layered on top of the filesystem DAL.
5. Git-backed updates use atomic git reset / edit / push cycles to apply changes to a central repository.

## Entity-Centric Data Model

1. Data is stored as **entities**, generally represented as JSON.
2. Every entity has:
   - a unique GUID `id`,
   - an `entityType`, and
   - shared and user-specific data payloads.
3. **Entity types are themselves entities** and define both:
   - schema constraints, and
   - metadata needed to display and manage entities of that type.
4. **Naming schemes are themselves entities**.
5. Entities include multiple non-unique identity vectors:
   - each vector is an alternate identity descriptor,
   - each vector is associated with a specific naming scheme entity.
6. The model is non-hierarchical by default:
   - entities are organized through relationships and structure, not strict parent/child trees.
7. Schema enforcement model:
   - each entity type defines/owns its schema,
   - schema validation is performed at entry time by a DAL schema-enforcement layer,
   - individual storage implementations do not each re-implement schema enforcement,
   - all schemas must set `unevaluatedProperties` to `false`.
8. Schema composition model:
   - a base entity schema defines common fields,
   - entity-specific schemas extend the base via `allOf`,
   - the final composed schema is the validation target,
   - the composed schema may be surfaced through an `anyOf` over the registered entity schemas when the system needs a top-level “any entity” validator.
9. Entity type composition model:
   - each entity exposes a `type` array of entity-type identifiers,
   - the entity must satisfy all schemas referenced by the `type` array,
   - validator composition is performed by the DAL/schema layer by translating the `type` array into a composed `allOf`.
10. Shared vs user-specific visibility:
   - each entity has a shared representation and per-user representation,
   - effective user-visible data comes from user-specific data,
   - users can always inspect shared and user data as separate underlying objects.
11. Relationships are modeled as entities:
   - a **relationship type** is an entity,
   - a relationship type defines the relationship schema and metadata needed to display/manage relationships of that type,
   - a relationship instance is itself an entity.
12. Relationship participation model:
   - a relationship generally references a set of participating entities,
   - participants are assigned into roles defined by the relationship type.
13. Referential integrity:
   - the DAL includes special logic to enforce referential integrity for relationships and their participants.
14. Interest relationships:
   - define a special class of relationships called **interests**,
   - include an **interest relationship type**,
   - interests associate user actions with creation/deletion of relationships.
15. Interest applicability model:
   - each entity type can specify related interests and the role the entity type plays for each,
   - each interest can specify associated entity types per relationship role,
   - the union of those role-based associations defines the entities to which an interest applies.
16. Relationship behavior scope:
   - interests are specifically about action-driven relationship create/delete behavior,
   - non-interest relationships can support additional behaviors beyond that automation.
17. Entity type identifiers and labels:
   - every entity type includes the unique IDs described above,
   - every entity type also includes a friendly-name identifier,
   - every entity type includes a display name for user-facing UI.

## Core and Predefined Types

0. Core system-provided schema types include:
   - `entity-id` - a guid. Entity ids maintain referential integrity across relationships and other references.
   - `timestamp` - a data-access-layer timestamp + value (Timestamp in IDataAccessLayer)
   - `localized-string` - a string with localization metadata as a dictionary from locale to string, with a required `default` locale and a required `id` locale and thematic styling information (color, etc)
1. Core system-provided entity types include (with associated fields below and derivation in ":", all entities derive from `entity`):
   - `entity`
     - `last-modified` timestamp,
     - `created` timestamp,
   - `view`
   - `jsonSchema`
     - `definition` JSON schema definition,
   - `entityType` : `jsonSchema`
   - `relationshipType`
   - `interest`
     - `relationship-type-id` referencing a `relationshipType` entity that defines the interest relationship type,
     - `badge-enabled` localized-string for the enabled state of the interest badge,
     - `badge-disabled` localized-string for the disabled state of the interest badge,
   - `entityTypeView`
   - `shortcutType`
   - `note`
     - `markdown` - Markdown content,
   - `json`
     - `json` - JSON content,
     - `schema-id` - reference to a `jsonSchema` entity defining the schema for the JSON content,
   - `task`
   - `workspace`
   - `external`
     - `url` - the external URL,
   - `user`
   - `computer`
   - `userProfile`
   - `workspaces-llm-session`
   - `workspaces-llm-conversation`
   - `workspaces-llm-conversation-event` (: `workspaces-llm-conversation-turn`, `note`, `json` as applicable)
     - `sequence` - the order of the event within the conversation,
     - `timestamp` - when the turn occurred,
     - `type` - the type of conversation event (e.g., user turn, llm turn, tool use, tool result, interruption, etc.)
   - `workspaces-llm-conversation-turn` (: `note`, `json` as applicable)
     - `speaker` - user or LLM,
   - `workspaces-llm-conversation-tool-use` (: `note`, `json` as applicable)
     - `tool-use-id`
     - `tool-server-name`
     - `tool-name`
   - `workspaces-llm-conversation-tool-result` (: `note`, `json` as applicable)
     - `tool-use-id`
     - `tool-server-name`
     - `tool-name`
   - `workspaces-llm-conversation-snapshot` : `note`, `workspaces-llm-conversation-event`
   - `workspaces-llm-conversation-environment-change` : `workspaces-llm-conversation-event`
     - `update-tool-server`
       - []
         - `tool-server-id`
         - `tool-server-name`
         - `configuration` - JSON blob with tool-server configuration details, or null to remove the tool server
     - `rights-change`
       - []
         - `add`
           - a set of `right` items to add
         - `remove`
           - a set of `right` items to remove
2. The system will provide a predefined set of relationship types and interest types.
3. One predefined relationship type is `ai-instructions`:
   - relates a set of target entities to a set of note entities.
4. Another predefined relationship type is `contains`:
   - directed from parent to child.
5. Predefined interest types include:
   - `assignment`
   - `ownership`
   - `blocked`
   - `actionable`
   - `interesting`
   - `not-interesting`
6. Interest relationships involve:
   - a user,
   - a target entity set,
   - a context entity set.
7. All relationships can include a set of `note` participants.

## Testing and Interface Strategy

1. Testing is a first-class requirement: we will test everything we can reasonably test.
2. Default architecture approach:
   - put facilities behind interfaces by default,
   - keep implementations replaceable and testable.
3. Primary testing model:
   - validate behavior through interface contracts,
   - run contract-focused tests across multiple implementations where applicable.

## View and GUI Architecture

1. The GUI is organized around a series of **views**.
2. Each view is defined as an entity.
3. The main user-facing view is itself a view entity:
   - default behavior uses a system-provided default view,
   - users can provide their own main view,
   - user-provided views can delegate to system-provided view lists.
4. We will define a dedicated **view data model** and **view entity type**.
5. Each view declares the set of sub-views it contains.
6. Some views represent entity collections in hierarchical list form.
7. Entity presentation is driven by entity-type-associated `entity_view` entities.
8. An `entity_view` entity provides display/interaction metadata, including:
   - fields to display,
   - shortcuts/actions to expose,
   - related objects to surface.
9. The GUI provides a basic arbitrary-entity surface called an **entity pane**.
10. An entity pane shows:
   - entity friendly name,
   - entity type,
   - applicable interest badges,
   - view-specific fields,
   - a bottom toggle strip for expanding relationship-linked child items,
   - a toggle arrow for expanding a complete entity view,
   - a set of shortcuts.
11. `shortcutType` defines shortcut behavior and rendering metadata, including:
   - handler type,
   - applicable entity types,
   - display properties.
12. Handler types are registered by extensions.
13. Shortcut handlers use URL syntax.
14. Entity types can specify applicable shortcut types.
15. Entity views can restrict which shortcuts and interest badges are shown.
16. Primary GUI layout is split:
   - entity/view browser on the left,
   - workspace view on the right.
17. `workspace` entity type:
   - defines the set of entities in the workspace through `contains` relationships,
   - defines display characteristics for those entities in a docking-oriented layout.
18. `open` is a predefined shortcut type applicable to:
   - workspaces, and
   - entities that provide URLs.
19. `external` is a predefined entity type representing links to external URLs.
20. When entities are opened in the workspace view, the workspace definition is updated accordingly.

## Classification Queue and Classifier Agent

1. Each user has a dedicated classification queue.
2. When an entity is modified, it is enqueued into that user's classification queue.
3. Classification is performed by an LLM agent acting on behalf of the user.
4. The classifier can:
   - inspect the entity,
   - modify other entities,
   - add or remove relationships,
   - execute user-specified instructions.
5. After an entity is processed by the classifier, it is removed from the queue.

## Initial External Integrations

1. **GitHub**
2. **Azure DevOps**
3. **Local machine tools** (user-installed/local executables and utilities)
