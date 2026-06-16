# Vector search

## Purpose

Enable semantic (vector) search over workspace entities so agents and the entity classifier can
find related entities and connections by meaning rather than exact text. Because computing
embeddings is expensive, indexing is **decoupled** from entity updates through a queue, and runs
as a scheduled tool (see `docs/design/scheduled-tools.md`).

## Scope: MongoDB (production) and in-memory (testing/dev)

The persistent vector index, embedding storage, and `$vectorSearch` query execution live in
`Phantom.Workspaces.Data.MongoDB` — MongoDB (Atlas Vector Search / `$vectorSearch`) provides the
approximate-nearest-neighbor index we rely on for production, and we do not maintain a second
production vector engine.

The **in-memory** data-access layer (`Phantom.Workspaces.Data.Offline.InMemoryDataAccessLayer`)
also supports query-clause evaluation and vector search via a brute-force cosine ranking over
entity text projections, computed at query time through an injected `IEmbeddingsProvider`
(defaulting to `DeterministicEmbeddingsProvider`). This makes the full query + vector pipeline
testable without MongoDB. Other data-access layers (git, web client) do not implement vector
search:

- They throw a clear `NotSupportedException` for unsupported clauses, or
- (web client) forward the request to a MongoDB-backed server that does.

## Decoupled indexing model

1. Entity updates flow through `IDataAccessLayer.UpdateAsync` as today; they do **not** compute
   embeddings inline.
2. A **vector indexer** scheduled tool pulls batches of recently-changed entities from a queue,
   computes their embeddings, and writes them back to the index.
3. Vector queries then run against the stored embeddings.

### Queue API

Queues are managed through `IDataAccessLayer`. A queue's head state is simply the **timestamp of
the last entity processed** in that queue.

```text
ProcessQueueAsync(request: { queueName, token?, count, get-request? }) -> { entities, token }
```

- If `token` is omitted, `ProcessQueue` reads the persisted head for `queueName`; if `token` is
  provided, it first advances the persisted head to that `token`.
- It then returns up to `count` entities in modified-timestamp order starting after the head, plus
  the `token` (timestamp) the caller should pass next time to acknowledge this batch.
- The token is a `Timestamp` (the same modified-time used elsewhere in the DAL).
- The "get-request" scopes entities to the given filter (for example, the entity classifier
  tool filters entities to within the current user).

This gives at-least-once processing with resumable progress: a crashed indexer re-reads the
unacknowledged batch on the next run.

### Embeddings API

```text
ComputeEmbeddingsAsync(request: { [entitySnapshot] }) -> { [embedding] }
UpdateEmbeddingsAsync(request: [{ entityId, concurrencyToken, embedding }]) -> { success }
```

- `ComputeEmbeddings` turns entity snapshots into embedding vectors via the configured
  **embeddings provider**. MIME / non-text content is handled specially by the provider; for now,
  non-text content is stripped before embedding.
- `UpdateEmbeddings` stores the computed vectors in the MongoDB vector index, keyed by entity id,
  and includes the `concurrencyToken` to ensure safe updates.

### Indexer flow

The vector indexer (a scheduled tool):

1. `ProcessQueueAsync({ queueName: "vector-index", count: #, get-request: {} })` to get the next batch.
2. `ComputeEmbeddingsAsync` for the batch.
3. `UpdateEmbeddingsAsync` to persist the vectors.
4. `ProcessQueueAsync({ queueName, token })` to acknowledge the batch (advance the head).

## Embeddings provider

`IEmbeddingsProvider` abstracts embedding computation so providers can be swapped:

```text
IEmbeddingsProvider
  Dimensions: int
  ModelId: string
  ComputeAsync(IReadOnlyList<EmbeddingInput>) -> IReadOnlyList<Embedding>
```

- `EmbeddingInput` is derived from an `EntitySnapshot`: a normalized, text-only projection of the
  entity (content plus selected fields), with MIME / binary parts stripped.
- The provider's `ModelId` and `Dimensions` are recorded with stored vectors so a model change can
  trigger reindexing and so queries use a matching query-embedding.

## Vector query integration

Vector search is expressed as a new clause in the existing query API (the `QueryClause` hierarchy
in `IDataAccessLayer.cs`), so it composes with the other clauses:

```text
Top:
  count: 10
  Query:
    And:
      Clause: <property / type filter>
      Clause: Vector { text | embedding, ... }
```

- A new `EntityVectorQueryClause : EntityQueryClause` carries either query text (embedded at query
  time via `IEmbeddingsProvider`) or a precomputed query embedding, plus optional parameters
  (number of candidates, minimum score). Like `EntityFullTextQueryClause`, it can contribute a
  per-clause relevance score surfaced through `FullTextQueryScore` / an analogous vector score.
- `TopQueryClause` bounds the result count, matching MongoDB `$vectorSearch` `limit`.

The clause is added to the C# model and to the data-access-layer JSON schema, with explicit
`[JsonPropertyName]` attributes consistent with the rest of the DTOs.

## MongoDB implementation

The container defaults to the **Atlas Local** image (`mongodb/mongodb-atlas-local`), which bundles
the `mongot` search process and therefore supports `$search` / `$vectorSearch` locally; the
community `mongo` image does not. The image is overridable via the container connection
definition's `image-name`.

`MongoDbQueryTranslator` converts a `QueryClause` tree into native MongoDB driver constructs using
**dynamic but secure** construction: every value is bound through the driver's
`FilterDefinitionBuilder` and `BsonValue` / `BsonRegularExpression` APIs (never string-interpolated),
so untrusted query text and field values are serialized as BSON literals and cannot inject query
operators. Full-text terms are matched as escaped, case-insensitive regular expressions.

The translator targets a denormalized **current-version projection** maintained on each entity
document write (`current.type-names`, `current.search-text`, `current.embedding`,
`current.is-deleted`).

In `MongoDbEntityDataAccessLayer`:

1. **Query (current)** — `QueryAsync` runs the translated `FilterDefinition` natively against the
   `current.*` projection for null-timestamp queries (entity-type, full-text, And/Or/Not, Top).
   As-of-timestamp querying is a follow-up.
2. **Index** — `EnsureVectorIndexAsync` creates/ensures a MongoDB vector search index over
   `current.embedding` (sized to the embeddings provider's `Dimensions`, `cosine` similarity). It is
   **self-healing**: if an index with the expected name exists but is in a terminal non-functional
   state (`DOES_NOT_EXIST` / `FAILED`, e.g. orphaned by dropping/recreating the collection), it is
   dropped (waiting for removal to settle) and recreated. *(Implemented.)*
3. **Storage** — embeddings are computed via the configured `IEmbeddingsProvider` and stored on the
   entity's `current.embedding` projection on each write. *(Implemented.)*
4. **Search** — `EntityVectorQueryClause` compiles to a `$vectorSearch` aggregation stage via
   `MongoDbQueryTranslator.BuildVectorSearchStage` (`queryVector`, `numCandidates` = `limit` × 10,
   `limit`, optional pre-filter from sibling clauses); `ExecuteVectorClauseAsync` runs it and
   projects the `vectorSearchScore` meta onto each match. *(Implemented and verified end-to-end
   against the Atlas Local container — see Test tasks.)*
5. **Queue state** — per-queue head timestamps are stored in a dedicated collection;
   `ProcessQueueAsync` reads/advances it and returns entities ordered by modified time *(follow-up)*.

### Atlas vector index eventual consistency

A freshly created Atlas vector index is **eventually consistent**: immediately after creation the
index reports transient states (`NOT_STARTED` → `PENDING`/`BUILDING` → `READY`) and queries against
it fail with phase-dependent errors ("Index … not initialized", "cannot query vector index … while
in state NOT_STARTED", "Search Index Management service" not ready). Callers that create an index and
query immediately must poll until the index becomes queryable; the MongoDB vector contract test does
this (see Test tasks).

## Relationship-as-note enforcement

To make connections searchable, every relationship created through the agent tooling must carry a
note: the agent tooling enforces that each created relationship is also of type `note`, so the
rationale for the connection is itself embeddable and discoverable by vector search.

## New classes

1. `IEmbeddingsProvider` / concrete provider(s) — compute embeddings from entity text.
   *(Implemented: `IEmbeddingsProvider`, `EmbeddingInput`/`Embedding`, `EntityTextProjection`, and
   `DeterministicEmbeddingsProvider` in `Phantom.Workspaces.Data.Vector`.)*
2. `EntityVectorQueryClause` — the vector query clause in the `QueryClause` hierarchy.
   *(Implemented, with `VectorQueryScore` surfaced on `QueryEntitySnapshot`.)*
3. `InMemoryQueryEvaluator` — in-memory query-clause + vector evaluation used by
   `InMemoryDataAccessLayer.QueryAsync`. *(Implemented.)*
4. `VectorIndexerTool` — scheduled tool driving the indexer flow (see scheduled-tools.md).
5. MongoDB DAL additions — `$vectorSearch` clause compilation, embedding storage on the current
   projection, and self-healing vector index management *(implemented)*; queue-state collection
   access *(follow-up)* — all within `Phantom.Workspaces.Data.MongoDB`.
6. `IDataAccessLayer` API additions — `ProcessQueueAsync`, `ComputeEmbeddingsAsync`,
   `UpdateEmbeddingsAsync` (MongoDB-backed; unsupported elsewhere).

## Key integration points

1. `IDataAccessLayer`
   - New queue/embedding methods and the vector query clause; only the MongoDB layer implements
     them.
2. Query API (`docs/design/llm-session.md` query clauses, `IDataAccessLayer.cs`)
   - `EntityVectorQueryClause` composes under `And` / `Top`.
3. Scheduled tools (`docs/design/scheduled-tools.md`)
   - `VectorIndexerTool` and the entity classifier consume `ProcessQueue` and vector queries.
4. Agent tooling
   - Exposes a vector-search query capability to agents and enforces relationship-as-note.
5. Embeddings configuration
   - The embeddings provider (model id, dimensions, endpoint/credentials) is configured like other
     providers; stored vectors record the model for reindex-on-change.

## Test tasks

1. `ProcessQueueAsync` tests — resumable batching by modified-time; token read/advance semantics;
   empty-queue and first-run (no token) behavior.
2. `UpdateEmbeddingsAsync` / storage tests — vectors upsert by entity id with model metadata.
3. `EntityVectorQueryClause` compilation tests — produces the expected `$vectorSearch` stage and
   composes with pre-filter clauses under `And` / `Top`.
4. Provider tests — MIME / non-text stripping; deterministic text projection from `EntitySnapshot`.
5. End-to-end (MongoDB integration) — `MongoDbDataAccessLayerVectorSearchContractTests` (tagged
   `SlowDocker`) runs the shared vector contract against a real **Atlas Local** container: it seeds
   entities, ensures the vector index, and **polls** the `$vectorSearch` query until the
   eventually-consistent index is queryable, then asserts nearest-neighbor ranking, scores, and
   candidate limiting. The in-memory derived contract test asserts the same semantics fast. The
   shared `MongoDbTestDatabaseFixture` reuses a **long-lived** container across runs (it is not
   destroyed on dispose) so the one-time Atlas Local replica-set + `mongot` search-service
   initialization is paid once rather than per run; only collection state is reset between tests.
   Non-MongoDB layers reject the vector APIs.
6. Relationship-as-note enforcement tests — agent-tool-created relationships are rejected unless
   typed as `note`.

## Non-goals

1. Vector search on non-MongoDB data-access layers.
2. Maintaining a separate/standalone vector database.
3. Inline (synchronous) embedding during entity updates — indexing is always queue-driven.
