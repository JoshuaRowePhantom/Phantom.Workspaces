# Azure DevOps integration

## Purpose

Bring Azure DevOps (ADO) organizations, projects, repositories, pull requests, pull-request
comment threads, and work items into the workspace as first-class entities, discovered and
kept up to date by scheduled discovery tools, and made **actionable** for the user through the
entity classifier. Pull requests and work items are also **tasks**; every Azure DevOps entity
is **external** (carries a URL). User identities on Azure DevOps are represented by a new
**`user-account`** entity type linked to the workspace `user`.

All Azure DevOps implementation code lives under `Phantom.Workspaces\Tools\AzureDevOps`.

## Entity types

| Entity type | Also is | External (URL) | Notes |
| --- | --- | --- | --- |
| `azure-devops-organization` | — | yes | Already scaffolded. The ADO org. |
| `azure-devops-project` | — | yes | Already scaffolded. A project in an org. |
| `azure-devops-repository` | — | yes | **New.** A Git repository in a project. |
| `azure-devops-pull-request` | `task` | yes | **New.** A PR in a repository. |
| `azure-devops-pull-request-comment-thread` | — | yes | **New.** A comment thread on a PR. |
| `azure-devops-work-item` | `task` | yes | Already scaffolded; add `task` composition + URL. |
| `user-account` | — | yes | **New, general-purpose.** A user's account on an external system (here, ADO). |

Existing scaffolding to reconcile (`schema-definitions/azure-devops-*-entity-type.json` +
`JsonSchemas/azure-devops-*.json`): `azure-devops-organization`, `azure-devops-project`, and
`azure-devops-work-item` already exist. `azure-devops-work-item.json` already composes
`task.json` + `external.json`. We add the three new Azure DevOps types and the `user-account`
type, and assign each a unique `entity-display-order` (per the entity-type display-order
convention in `entity-editor.md`).

### Why `user-account` (revised)

Earlier drafts added a fourth name component directly to the `user` entity
(`["users", "azure-devops", <organization>, <user-name>]`). Instead, **a dedicated
`user-account` entity** holds external-account identity, so:

- The `user` entity stays clean (identity facts about external systems live on the account).
- One workspace `user` can own many accounts (ADO orgs, GitHub, etc.); each is a separate
  `user-account` linked to the user.
- Comment-thread / PR participant identities resolve to a `user-account` first, then to the
  owning `user`, which is what lets the classifier decide "is this directed at *me*?"

## Naming scheme

Hierarchical names, each entity using a prefix of the Azure DevOps path:

| Entity | Primary name |
| --- | --- |
| organization | `["azure-devops", <organization>]` |
| project | `["azure-devops", <organization>, <project>]` |
| repository | `["azure-devops", <organization>, <project>, <repository>]` |
| pull-request | `["azure-devops", <organization>, <project>, <repository>, <pull-request-id>]` |
| comment-thread | `["azure-devops", <organization>, <project>, <repository>, <pull-request-id>, <comment-thread-id>]` |
| work-item | `["azure-devops", <organization>, <project>, "work-items", <work-item-id>]` |
| user-account | `["user-accounts", "azure-devops", <organization>, <user-name>]` |

- Work items are project-scoped (not under a repository), so they use a `"work-items"`
  discriminator segment to avoid colliding with repository names.
- `user-account` uses a `"user-accounts"` prefix (a new default-name-prefix on the type). The
  Azure DevOps user identity (`<user-name>`, the ADO unique name / UPN) is stored on the
  account, not on the `user`.
- Entity references are entity-name arrays, never slash-joined strings (repo convention).

## Schemas

Each new type gets: a `JsonSchemas/<name>.json` schema, a `schema-definitions/<name>-entity-type.json`
entity-type entity (the existing scaffolding pattern), and a `documentation/<name>-schema.md`
note. All schemas are documented with descriptions per the schema-documentation convention.

Composition (`allOf`):

- `azure-devops-repository.json` → `entity.json` + `external.json`; fields: `repository-id`
  (GUID), `default-branch`, `project` (reference to the project entity).
- `azure-devops-pull-request.json` → `entity.json` + `task.json` + `external.json`; fields:
  `pull-request-id`, `repository` (reference), `status` (`active`/`completed`/`abandoned`),
  `is-draft`, `source-branch`, `target-branch`, `author` (reference to a `user-account`),
  `reviewers` (array of `{ account: <user-account ref>, vote: approved | approved-with-suggestions
  | waiting | rejected | no-vote }`), `last-iteration-id`, `merge-status`, and a
  `build-status` summary (`succeeded` / `failed` / `partially-succeeded` / `none`). `task`
  `status` is mapped from the ADO PR state.
- `azure-devops-pull-request-comment-thread.json` → `entity.json` + `external.json`; fields:
  `comment-thread-id`, `pull-request` (reference), `status` (`active` / `fixed` / `wont-fix`
  / `closed` / `pending`), `is-resolved`, `comments` (array of `{ author: <user-account ref>,
  text, published-date, content-hash }`), and `thread-context` (file path / line, when the
  thread is on code).
- `azure-devops-work-item.json` (existing) → add nothing new beyond confirming `task` +
  `external`; add `assigned-to-account` (reference to a `user-account`) alongside the existing
  source `assigned-to` raw string that the classifier already understands.
- `user-account.json` → `entity.json` + `external.json`; fields: `system` (e.g.
  `"azure-devops"`), `organization`, `account-name` (the ADO unique name), `display-name`,
  and `user` (reference to the owning workspace `user`, `x-entity-types: ["user"]`). The
  external `urls.default` is the account's ADO profile URL.

### URLs (external)

Every Azure DevOps entity sets `urls.default` to its web URL so it opens in the external
browser view (`external-entity-browser-view.md`):

- organization → `https://dev.azure.com/<org>`
- project → `https://dev.azure.com/<org>/<project>`
- repository → `https://dev.azure.com/<org>/<project>/_git/<repo>`
- pull-request → `.../_git/<repo>/pullrequest/<id>`
- comment-thread → PR URL with the thread anchor
- work-item → `https://dev.azure.com/<org>/<project>/_workitems/edit/<id>`
- user-account → the user's ADO profile URL

## Hierarchy and relationships

The Azure DevOps tree is expressed two ways, which agree:

1. **Hierarchical names** (above) — cheap prefix-based grouping.
2. **`reference` relationships** linking each child to its contextual parent
   (thread → pull-request → repository → project → organization, and work-item → project).
   The entity-type-views for these types declare that parent relationship in their
   `parent-hierarchy-relationships`, so the standard view renders contextual parents/children
   (consistent with the data-driven view design). Discovery maintains these relationships.

`user-account` → `user` is also a relationship (and a `user` reference field on the account),
so accounts appear under their user and the classifier can resolve account → user.

## Discovery tools

All discovery tools implement `IWorkspaceTool` (`ToolType` string), are registered as `tool`
entities (`tool-type` matching), run on a schedule like the existing Git scan tools, and upsert
entities through `WorkspaceToolEntityUtilities.UpsertEntityByPrimaryNameAsync` (deterministic
IDs from the primary name). They read the org/project to scan from their participant entities
(the `azure-devops-organization` / `azure-devops-project` entities) and the current user.

### Seeding: user asks the LLM to create org + project

The user creates the **organization** and **project** entities by asking the agent (the
workspace entity toolset can create entities). For example, "add my Azure DevOps org `contoso`
and project `payments`." The agent creates:

- an `azure-devops-organization` entity named `["azure-devops", "contoso"]`, and
- an `azure-devops-project` entity named `["azure-devops", "contoso", "payments"]` referencing
  the organization.

These seed entities are the participants the discovery tools scan from. No repositories,
pull requests, or work items are created by hand — the discovery tools fill those in.

### `azure-devops-repository-discovery`

- For each `azure-devops-project` participant, lists the project's Git repositories via the
  ADO REST API and **automatically upserts an `azure-devops-repository` entity for every
  repository in the project**, with the repository → project `reference` relationship.

### `azure-devops-work-item-discovery`

- Discovers **all work items the user has participated in** (created, assigned, mentioned,
  followed, or commented on) — via the ADO work-item query (WIQL) / "my activity" APIs scoped
  to the project — and upserts an `azure-devops-work-item` entity for each (with `task`
  status mapped from the ADO state, and `assigned-to-account` resolved to a `user-account`).

### `azure-devops-pull-request-discovery`

- Discovers pull requests that are **either** (a) non-terminal (status `active`, not
  completed/abandoned) PRs the user has participated in (author, reviewer, or commenter),
  **or** (b) any PR for which an `azure-devops-pull-request` entity already exists (so existing
  tracked PRs keep updating even after they become terminal, until classified away).
- For each PR, upserts the `azure-devops-pull-request` entity and **its comment threads** as
  `azure-devops-pull-request-comment-thread` entities, with reference relationships
  (thread → PR → repository). Captures reviewer votes, last iteration id, build status, and
  thread resolution state — the inputs the classifier needs for actionability.

### Identity discovery (user-account)

- As part of discovery, every Azure DevOps identity encountered (PR author, reviewers, thread
  comment authors, work-item assignee) is upserted as a `user-account` entity named
  `["user-accounts", "azure-devops", <organization>, <user-name>]`.
- The **current** user's ADO account is additionally linked to the workspace `user` entity
  (reference + relationship), so "directed at me" resolves correctly. Other accounts are
  linked to their users opportunistically when a matching `user` is known.

## Authentication and configuration

- **Token resolution** mirrors the GitHub-token approach: an `AzureDevOpsTokenResolver`
  resolves a credential from a predefined environment variable
  (`AZURE_DEVOPS_EXT_PAT`, the standard ADO PAT variable) and falls back to an Azure CLI
  access token (`az account get-access-token --resource <ADO resource>`). The token source is
  **not** an exposed, user-configurable env-var field in the UI (same rule as the GitHub
  token).
- **No secrets in the repository.** Only token *sources* are referenced; raw PATs never appear
  in tracked files or entity data.
- **Scope configuration** (which org/project) lives on the `azure-devops-organization` /
  `azure-devops-project` entities (and the tool's participants), not in code.

## Azure DevOps client/service layer

Under `Phantom.Workspaces\Tools\AzureDevOps`:

- `IAzureDevOpsClient` — a thin async wrapper over the ADO REST API (repositories, pull
  requests, threads/comments, work items, identities, build status). All calls async,
  cancellation-aware, no GUI blocking.
- `AzureDevOpsClient` — `HttpClient`-based implementation; authenticates via
  `AzureDevOpsTokenResolver`. Paginates list endpoints.
- `AzureDevOpsTokenResolver` — credential resolution (env var → Azure CLI), unit-testable.
- DTO/record types for the REST payloads kept internal to the AzureDevOps folder; mapping to
  entity JSON is done by the discovery tools.
- The client is injected into the discovery tools (interface-first) so tools are tested against
  a fake client with canned responses — no network in unit tests.

> The C# files compile via the SDK's default `**/*.cs` glob, so the new
> `Tools\AzureDevOps\*.cs` files are picked up without a `Phantom.Workspaces.csproj` change
> (the existing explicit `Tools\*.cs` include only reorders the top-level Tools files).

## Entity classifier: actionability instructions

The classifier already runs per changed entity and can apply the `actionable` interest
(a relationship with `target` = the entity and `user` = the user). To tell it **how and when**
to mark Azure DevOps pull requests / comment threads actionable, we put the instructions in the
**note content of the entity-type entities** and have the classifier surface them.

### Mechanism

- Entity-type entities are already `note` entities with markdown `content` (today used for
  schema documentation). The classification guidance for a type is written into that **note
  content** — no new schema field is introduced.
- Extend `EntityClassifierTool` to load, for the entity being classified, the **note content
  of each of its entity-types** and include it in the assembled prompt, placed after the global
  interest instructions and before the entity content (KV-cache-friendly: per-type text is
  stable across a run of same-typed entities). This is additive and changes nothing when a
  type's note has no classification guidance.

### Pull-request actionability rules (in the `azure-devops-pull-request` type's note)

Mark the PR `actionable` for the user when any of the following holds; otherwise remove the
`actionable` interest (and mark long-terminal PRs `not-interesting`):

- A comment is **directed at the user** (mentions them) or is added to a **PR the user owns**
  (author = the user's account).
- The **author has resolved a thread** that the user opened/participated in and the user needs
  to **verify** the resolution.
- The author has **pushed a new iteration** (`last-iteration-id` increased) on a PR the user
  reviews.
- The **build pipeline has an error** (`build-status` = `failed` / `partially-succeeded`).
- The PR has been **signed off** (required reviewers approved → user may complete it) or
  **rejected** (a reviewer voted `rejected` → author must act).
- (Extensible — the instruction note lists these as examples and lets the classifier
  generalize.)

### Comment-thread actionability rules (in the `azure-devops-pull-request-comment-thread` type's note)

- A thread is relevant when it is **unresolved** and **the user is a participant** (author of
  the PR, a reviewer, or previously commented), especially when the **latest comment is not by
  the user** (someone is waiting on them).
- Resolution by the PR author of a thread the user raised → flag for the user to **verify**.
- Threads drive PR actionability: an actionable thread should make its parent PR actionable
  (the classifier follows the thread → PR reference).

These instructions reference user identity via `user-account` → `user`, so "directed at the
user" and "the user owns this PR" are decidable.

## Scheduling

The three discovery tools are registered as default `tool` entities with schedules (reuse the
existing schedule entities, e.g. every 15 minutes for PRs/threads, hourly for repositories and
work items). Discovery is incremental and idempotent (deterministic IDs + upsert), so repeated
runs converge.

## Source layout (`Phantom.Workspaces\Tools\AzureDevOps`)

- `AzureDevOpsRepositoryDiscoveryTool.cs`, `AzureDevOpsWorkItemDiscoveryTool.cs`,
  `AzureDevOpsPullRequestDiscoveryTool.cs` — the `IWorkspaceTool` discovery tools.
- `IAzureDevOpsClient.cs`, `AzureDevOpsClient.cs`, `AzureDevOpsTokenResolver.cs` — service layer.
- `AzureDevOpsEntityNames.cs` — helpers that build the hierarchical `EntityName`s above.
- `AzureDevOpsEntityFactory.cs` — maps REST DTOs → entity `JsonObject`s (names, types, urls,
  references), shared by the tools.
- DTO records (internal).

Default entities/schemas (in `Phantom.Workspaces.Data.Core`):

- `JsonSchemas/azure-devops-repository.json`, `.../azure-devops-pull-request.json`,
  `.../azure-devops-pull-request-comment-thread.json`, `.../user-account.json`.
- `JsonEntities/schema-definitions/*-entity-type.json` for each new type (with unique
  `entity-display-order`, and classification guidance written into the type's note `content`
  where applicable).
- `JsonEntities/documentation/*-schema.md` notes.
- `JsonEntities/defaults/tools/azure-devops-*-discovery-tool.json` tool entities + schedules.

## High-level code changes

- **New (`Tools\AzureDevOps\`):** the three discovery tools, `IAzureDevOpsClient` /
  `AzureDevOpsClient`, `AzureDevOpsTokenResolver`, `AzureDevOpsEntityNames`,
  `AzureDevOpsEntityFactory`, DTOs.
- **Tool registration:** add the three `tool-type`s to the tool factory/registry that maps
  `tool-type` → `IWorkspaceTool` (alongside `git-workspace-scan`, `user-discovery`, etc.).
- **`EntityClassifierTool`:** load and include the classified entity's entity-types' note
  `content` in the assembled prompt (the classification guidance lives in those notes).
- **Schemas/entities (`Data.Core`):** new JsonSchemas, entity-type entities, docs, tool +
  schedule entities (above); reconcile `azure-devops-work-item` (`assigned-to-account`).
- **Views (optional):** an Azure DevOps view-definition and entity-type-views declaring the
  parent-hierarchy relationships, so the tree renders.
- **Docs:** update `architecture.md`'s tool/integration sections to list Azure DevOps.

## Tests to write

Schema/data:

- Each new JsonSchema accepts a representative valid entity and rejects malformed ones
  (missing required ids, bad `status` enum).
- `azure-devops-pull-request` and `azure-devops-work-item` validate as `task` + `external`
  (composition); all Azure DevOps types validate as `external` with a `urls.default`.
- Every Azure DevOps + `user-account` entity-type entity has a unique `entity-display-order`
  (extends the existing display-order uniqueness test).
- Name builders produce the documented hierarchical names (`AzureDevOpsEntityNames` tests).

Discovery tools (against a fake `IAzureDevOpsClient`, no network):

- Repository discovery upserts one `azure-devops-repository` per project repo, with the
  repo → project reference; idempotent on re-run.
- Work-item discovery upserts work items the user participated in, maps `task` status, and
  resolves `assigned-to-account` to a `user-account`.
- Pull-request discovery includes non-terminal participated PRs **and** PRs with an existing
  entity; excludes terminal PRs with no existing entity; upserts threads with thread → PR
  references; captures reviewer votes / iteration id / build status.
- Identity discovery upserts `user-account` entities for all encountered identities and links
  the current user's account to the `user` entity.

Auth:

- `AzureDevOpsTokenResolver` prefers `AZURE_DEVOPS_EXT_PAT`, falls back to Azure CLI, and
  surfaces a clear error when neither is available; never logs the token.

Classifier:

- The note `content` of an entity's entity-types is included in the assembled prompt in the
  documented order, and types whose notes have no guidance add nothing.
- Given a fake agent runner, a PR with a failed build / a rejected vote / a new iteration /
  a mention is marked `actionable` (relationship created with `target` = PR, `user` = user);
  a terminal, untouched PR is marked `not-interesting`; an actionable thread makes its parent
  PR actionable.

Determinism: all tests use fakes/canned responses and event-driven assertions — no network,
no timing-based waits.

## Open questions

1. **Identity matching.** How aggressively to auto-link non-current `user-account`s to existing
   `user` entities (exact UPN match only, vs. fuzzy)? Proposed: exact account-name/UPN match,
   else leave unlinked.
2. **Work-item "participated in" scope.** Exact set of ADO activity signals to include
   (assigned, created, mentioned, followed, commented). Proposed: all of these, via the
   "my work items"/WIQL activity queries.
3. **Build status granularity.** Per-PR summary vs. per-pipeline detail entities. Proposed:
   start with a summary field on the PR; add pipeline entities later if needed.
4. **Auth for multiple orgs.** One PAT vs. per-org credentials. Proposed: per-org token source
   resolution keyed by organization, defaulting to the standard env var / Azure CLI.
