# Vector search

## Purpose

Enable semantic (vector) search over workspace entities so agents and the entity classifier can
find related entities and connections by meaning rather than exact text. Because computing
embeddings is expensive, indexing is **decoupled** from entity updates through a queue, and runs
as a scheduled tool (see `docs/design/scheduled-tools.md`).

## Scope: MongoDB only

Vector search is supported **only on the MongoDB data-access layer**. The vector index, embedding
storage, and `$vectorSearch` query execution live in `Phantom.Workspaces.Data.MongoDB`. Other
data-access layers (in-memory, git, web client) do not implement vector search:

- They throw a clear `NotSupportedException` for the vector APIs / a vector query clause, or
- (web client) forward the request to a MongoDB-backed server that does.

This is an intentional constraint: MongoDB (Atlas Vector Search / `$vectorSearch`) provides the
approximate-nearest-neighbor index we rely on; we do not maintain a second vector engine.

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
ProcessQueueAsync(request: { queueName, token?, count }) -> { entities, token }
```

- If `token` is omitted, `ProcessQueue` reads the persisted head for `queueName`; if `token` is
  provided, it first advances the persisted head to that `token`.
- It then returns up to `count` entities in modified-timestamp order starting after the head, plus
  the `token` (timestamp) the caller should pass next time to acknowledge this batch.
- The token is a `Timestamp` (the same modified-time used elsewhere in the DAL).

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

1. `ProcessQueueAsync({ queueName: "vector-index", count })` to get the next batch.
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

In `MongoDbEntityDataAccessLayer`:

1. **Index** — a MongoDB vector search index over the embedding field (per embeddings `ModelId` /
   `Dimensions`), created/ensured on startup.
2. **Storage** — `UpdateEmbeddingsAsync` upserts the vector (and its model metadata) onto the
   entity's document/sidecar collection.
3. **Search** — an `EntityVectorQueryClause` compiles to a `$vectorSearch` aggregation stage
   (`queryVector`, `numCandidates`, `limit`, optional pre-filter from sibling clauses), returning
   matches with their similarity scores.
4. **Queue state** — per-queue head timestamps are stored in a dedicated collection;
   `ProcessQueueAsync` reads/advances it and returns entities ordered by modified time.

## Relationship-as-note enforcement

To make connections searchable, every relationship created through the agent tooling must carry a
note: the agent tooling enforces that each created relationship is also of type `note`, so the
rationale for the connection is itself embeddable and discoverable by vector search.

## New classes

1. `IEmbeddingsProvider` / concrete provider(s) — compute embeddings from entity text.
2. `EntityVectorQueryClause` — the vector query clause in the `QueryClause` hierarchy.
3. `VectorIndexerTool` — scheduled tool driving the indexer flow (see scheduled-tools.md).
4. MongoDB DAL additions — `$vectorSearch` clause compilation, embedding upsert, queue-state
   collection access, and vector index management (within `Phantom.Workspaces.Data.MongoDB`).
5. `IDataAccessLayer` API additions — `ProcessQueueAsync`, `ComputeEmbeddingsAsync`,
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
5. End-to-end (MongoDB integration) — index a small set, query by text, assert nearest neighbors
   and scores; assert non-MongoDB layers reject vector APIs.
6. Relationship-as-note enforcement tests — agent-tool-created relationships are rejected unless
   typed as `note`.

## Non-goals

1. Vector search on non-MongoDB data-access layers.
2. Maintaining a separate/standalone vector database.
3. Inline (synchronous) embedding during entity updates — indexing is always queue-driven.
