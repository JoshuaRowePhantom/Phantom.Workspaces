# Workspace Entity Tool Instruction Details

## Get one entity by id

1. Call `workspace_entity_get_by_id` with `entity-id`.
2. Read `entity` from the JSON result.
3. Save `entity.concurrencyTag` for updates or deletes.

## Get one entity by name

1. Call `workspace_entity_get_by_name` with `entity-name` as an ordered string array.
2. Read `entity` from the JSON result.
3. Save `entity.concurrencyTag` for updates or deletes.

## Add a new entity

1. Generate a new GUID for `entity-id`.
2. Build the full entity JSON object in `data`.
3. Call `workspace_entity_add` with `entity-id`, `data`, and `comment`.
4. Confirm `entityResults[0].updateState` is `Added`.

## Update an existing entity

1. Read the current entity first with `workspace_entity_get_by_id` or `workspace_entity_get_by_name`.
2. Copy the current `entity.concurrencyTag`.
3. Build the full replacement object in `data`.
4. Call `workspace_entity_replace` with `entity-id`, `concurrency-tag`, `data`, and `comment`.
5. Confirm `entityResults[0].updateState` is `Updated`.

## Delete an entity

1. Read the current entity first with `workspace_entity_get_by_id` or `workspace_entity_get_by_name`.
2. Copy the current `entity.concurrencyTag`.
3. Call `workspace_entity_delete` with `entity-id`, `concurrency-tag`, and `comment`.
4. Confirm `entityResults[0].updateState` is `Removed`.

## Search for an entity

1. Build candidate entity names.
2. Call `workspace_entity_get_by_name` for each candidate `entity-name`.
3. Keep entities where the returned `entity` is not null.

## Get schema definitions for entity types

1. For entity type `<type-name>`, call `workspace_entity_get_by_name` with `entity-name` = `["entity-types", "<type-name>"]`.
2. Read `entity.data.schema`.

## Get documentation for entity types

1. For entity type `<type-name>`, call `workspace_entity_get_by_name` with `entity-name` = `["entity-types", "<type-name>"]`.
2. Read markdown text from `entity.data.content.default.content.text`.

## Get all entity types

Use the following built-in entity type names:

- `azure-devops-organization`
- `azure-devops-project`
- `azure-devops-work-item`
- `computer`
- `core`
- `entity`
- `entity-type`
- `entity-type-view`
- `external`
- `filesystem-path`
- `folder`
- `git`
- `git-worktree`
- `interest`
- `json-schema`
- `llm-trust-profile`
- `mime-attachment`
- `note`
- `reference`
- `related`
- `relationship`
- `relationship-type`
- `schedule`
- `task`
- `tool-relationship`
- `user`
- `user-computer-profile`
- `view`
- `workspace`
- `workspaces-profile`
