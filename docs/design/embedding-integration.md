Design: Embedding Integration for Phantom.Workspaces

Problem
- Current system lacks a configurable, production-ready embeddings integration for semantic search, similarity, and retrieval-augmented workflows.

Goals
- Provide a pluggable provider abstraction for embeddings.
- Keep default option low-cost and simple to run locally.
- Allow easy opt-in to hosted / paid providers.
- Support batching, caching, and vector store backends.
- Securely manage keys and telemetry, and make usage observable.

Constraints & Cost Considerations
- Minimize per-request costs by batching and caching embeddings.
- Support open-source & self-hosted models (sentence-transformers, HuggingFace with ONNX/quantized runtimes) for offline use.
- Allow cloud providers (OpenAI/Azure/Anthropic/Pinecone/Vector DB) as optional configured providers — documented tradeoffs.
- Prefer pay-as-you-go for production deployments; default local option avoids cloud costs for small teams.

High-level Architecture
- EmbeddingProvider (interface)
  - Task: produce embeddings for texts (single and batch), return vector float32[] or quantized representation, and metadata (model id, latency)
  - Implementations:
    - LocalProvider: wraps an on-host model (HuggingFace sentence-transformers via .NET binding or a lightweight Python microservice)
    - CloudProvider: wraps OpenAI/Azure/Anthropic embedding endpoints
    - VectorDB-backedProvider: optional provider that also persists vectors as it computes (for streaming ingestion flows)

- EmbeddingService (core)
  - Exposes app-level API used by other services (indexer, search, RAG pipelines)
  - Responsibilities:
    - Input normalization (token/length heuristics)
    - Batching and concurrency control
    - Caching layer (local LRU cache + optional Redis cache for multi-instance)
    - Metrics and telemetry
    - Retry/backoff & cost-protection guardrails (max tokens per batch, request rate limits)

- Vector Store Adapters
  - Provide pluggable adapters: PGVector (Postgres), Milvus, Weaviate, FAISS-on-disk, and in-memory for tests
  - Migration plan for existing indices (if any)

Configuration and Runtime
- Central config: Embedding configuration is stored in a shared database table (EmbeddingsConfig) for multi-instance deployments; do not rely on per-instance appsettings.json for production.
  - EmbeddingsConfig table (suggested columns): Provider TEXT, Model TEXT, BatchSize INT, CacheEnabled BOOL, CacheTtlSeconds INT, SecretsRef TEXT, Version INT, UpdatedAt TIMESTAMP, UpdatedBy TEXT
  - Secrets are stored in a secrets manager (Key Vault/SecretStore) and referenced via SecretsRef; the DB contains only secret references (never API keys in plaintext).
- Runtime behavior:
  - On startup, EmbeddingService reads DB config and caches it locally.
  - Multi-instance config changes propagate via Postgres LISTEN/NOTIFY or Redis Pub/Sub to invalidate caches.
  - Provide optimistic concurrency via Version and an admin API/UI to change settings.
  - Provide a bootstrap fallback to appsettings.json for single-node dev (explicit opt-in).

Security & Operational Notes
- Do not log the raw inputs or API keys; redact them from telemetry.
- Add cost alerts/usage export for cloud providers.
- Provide a diagnostics endpoint (restricted) to show model, last successful call time, queue lengths.

Developer Experience
- Provide a lightweight local dev experience via a small Python service (optional) that loads a prepackaged small embedding model so devs can run without cloud keys.
- Unit tests: mock EmbeddingProvider and run deterministic tests for batching, caching, error handling.
- Integration tests: a small in-memory vector store with known vectors to assert similarity correctness.

Recommendation (cost-effective)
- Default: LocalProvider using an open-source sentence-transformers model run via a small service (Python + ONNX/quantized) with small model (e.g., all-MiniLM) for low-cost local usage.
- Production: Allow CloudProvider opt-in. Encourage caching and batching to reduce API calls and costs.
- Vector storage: start with PGVector (Postgres) for low infra overhead; add Milvus/Weaviate adapters if scale demands it.

Implementation Plan (high level)
1. Define EmbeddingProvider interface and EmbeddingService facade.
2. Implement LocalProvider wrapper (dev-only PoC) and unit tests.
3. Implement CloudProvider for OpenAI with batching and backoff; add metrics.
4. Add caching layer and PGVector adapter.
5. Add configuration, secrets guidance, and diagnostic endpoints.
6. Document migration and usage in docs/ and README.

Acceptance Criteria
- A config-driven provider switch exists and is exercised in unit tests.
- EmbeddingService supports batched calls and caching.
- Local dev mode works without cloud keys.
- Documentation and the enhancement issue track remaining work.

Files & References
- design/embedding-integration.md (this file)
- Example config snippet (below)

Example config snippet (appsettings.json):
{
  "Embeddings": {
    "Provider": "local",
    "Model": "all-MiniLM-L6-v2",
    "BatchSize": 32,
    "CacheEnabled": true,
    "CacheTtlSeconds": 86400
  }
}

Todos (tracked in issue):
- define interface and public API surface
- implement local dev provider
- implement OpenAI provider
- implement PGVector adapter
- add caching and metrics
- write docs and runbook

Notes
- Consider a small Python embedding microservice to avoid heavy .NET-native model bindings initially; provide a gRPC/HTTP adapter.
- Keep provider implementations small and well-tested to enable swapping without breaking caller contracts.

