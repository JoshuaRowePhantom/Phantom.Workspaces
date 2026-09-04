# Embedding Integration for Phantom.Workspaces

This design describes a pluggable embeddings system with DB-backed configuration, runtime model fetching, provider lifecycle (switching and reindexing), and per-instance GPU gating. It includes C# class/method signatures and concrete SQL schemas to guide implementation.

## Core concepts
- EmbeddingsConfig: authoritative, shared configuration for which provider/model to use (DB-backed)
- ModelArtifact: downloadable model artifact metadata and status
- IEmbeddingProvider: runtime adapter that computes embeddings
- EmbeddingService: application façade that callers use to request embeddings and which handles provider resolution, caching, batching
- ModelFetcher: background component that downloads/validates model artifacts
- ReindexJob: a job to regenerate vectors when a provider changes

## Public C# API (recommended names)

```csharp
public interface IEmbeddingProvider
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
    Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> inputs, CancellationToken ct = default);
    string ModelId { get; }
    bool SupportsGpu { get; }
}

// EmbeddingService: pure service for generating embeddings only.
// It must not own or perform config switching; it resolves the active provider
// from the configured runtime provider factory and focuses on batching, cache and telemetry.
public class EmbeddingService
{
    // Get embeddings; will use provider resolution + batching + cache
    public Task<float[]> GetEmbeddingAsync(string text, EmbeddingOptions? options = null, CancellationToken ct = default);
    public Task<float[][]> GetEmbeddingsAsync(IReadOnlyList<string> inputs, EmbeddingOptions? options = null, CancellationToken ct = default);

    // UI hook for provider/health status (e.g., provider unavailable, degraded)
    public event Action<EmbeddingsStatusChangedEventArgs>? OnStatusChanged;
}

public record EmbeddingOptions(bool StrictPrimaryProvider = false);

// ModelFetcher: downloads and materializes model artifacts to local cache
public interface IModelFetcher
{
    Task EnqueueAsync(Guid artifactId, CancellationToken ct = default);
    Task<ModelArtifactStatusDto> GetStatusAsync(Guid artifactId, CancellationToken ct = default);
}

// --- New: configuration & orchestration responsibilities moved into dedicated components ---

// SystemConfig store (generic)
public interface ISystemConfigStore
{
    // Gets a JSON blob stored at the hierarchical key (e.g. ["system","config","embeddings"]).
    Task<BsonDocument?> GetAsync(string[] keyPath, CancellationToken ct = default);
    Task SetAsync(string[] keyPath, BsonDocument value, CancellationToken ct = default);
    Task<bool> TryUpdateAsync(string[] keyPath, Func<BsonDocument?, BsonDocument> update, CancellationToken ct = default);
}

// EmbeddingsConfigManager: manages the stored EmbeddingsConfig (read/update) and
// exposes operations to validate, stage, and commit a config switch. It does NOT
// perform embedding generation; it orchestrates ModelFetcher + Reindex job creation.
public interface IEmbeddingsConfigManager
{
    // Read the active config from the system config key ["system","config","embeddings"]
    Task<EmbeddingsConfig?> GetConfigAsync(CancellationToken ct = default);

    // Atomically set or replace the config. Returns the new config id/version.
    Task<EmbeddingsConfig> SetConfigAsync(EmbeddingsConfig config, CancellationToken ct = default);

    // Validate config (provider reachability, option validation). Does not make changes.
    Task<ConfigValidationResult> ValidateAsync(EmbeddingsConfig config, CancellationToken ct = default);

    // Orchestrate a config switch: validate -> create ModelArtifact if needed -> enqueue ModelFetcher -> create ReindexJob
    // This operation returns an orchestration token (operation id) that can be polled for progress.
    Task<Guid> StageAndSwitchAsync(EmbeddingsConfig desiredConfig, CancellationToken ct = default);

    // Query orchestration status (artifact download + reindex progress)
    Task<OrchestrationStatusDto> GetOrchestrationStatusAsync(Guid orchestrationId, CancellationToken ct = default);
}

// Helper DTOs
public record ConfigValidationResult(bool IsValid, string? ErrorMessage = null);
public record OrchestrationStatusDto(Guid OrchestrationId, string Status, int ProgressPct, string? LastError = null);
```


## Provider implementations (suggested classes)
- LocalEmbeddingProvider : IEmbeddingProvider
  - ctor: LocalEmbeddingProvider(string artifactPath, OnnxOptions options, ILogger logger)
  - Uses ONNX Runtime or TorchSharp; respects OnnxOptions.UseGpu
- OpenAIEmbeddingProvider : IEmbeddingProvider
  - ctor: OpenAIEmbeddingProvider(string secretsRef, OpenAiProviderOptions options, IHttpClientFactory, ILogger)
  - Implements batching, retries, telemetry, spend caps
- ModelServerEmbeddingProvider : IEmbeddingProvider
  - ctor: ModelServerEmbeddingProvider(Uri endpoint, HttpClient client, ILogger)

## Provider lifecycle & switching
- SwitchConfigAsync flow:
  1. Increment EmbeddingsConfig.version (optimistic concurrency).
  2. If local provider: create ModelArtifact (status='queued') and Enqueue ModelFetcher.
  3. Create a ReindexJob (status='pending') scoped per request.
  4. Notify UI via OnStatusChanged.

- Reindex strategy:
  - ReindexJob contains scope, priority, dry-run flag.
  - Worker (EmbeddingsGenerator) processes job and writes new document_vector rows with provider_config_id == new config id.
  - After success, switch active vector pointer atomically.

## Chicken-and-egg (query API)
- Two-tier resolution: Primary (configured) and Fallback (bundled small model).
- GetEffectiveProvider():
  - If primary EmbeddingsConfig references cloud provider: instantiate cloud provider.
  - If primary references ModelArtifact and artifact.status == 'ready': instantiate LocalEmbeddingProvider.
  - Otherwise, return bundled LocalEmbeddingProvider (fallback).
- EmbeddingOptions.StrictPrimaryProvider forces an error when primary is not ready.

## Per-instance GPU setting
- Local setting: Settings.UseGpuForEmbeddings (persisted locally per install, default true).
- EmbeddingService recreates provider when this setting changes.
- Runtime provider creation checks: UseGpuForEmbeddings && GpuProbe.HasGpu && artifact.SupportsGpu
- Do not store hardware flags in EmbeddingsConfig.

## Model download & monitoring
- ModelFetcher responsibilities:
  - Download to temporary file, verify checksum, atomically move to node cache
  - Update model_artifact.status and ready_at
  - Emit DB NOTIFY / Redis pubsub for multi-instance propagation
  - Only one downloader per-host (OS mutex / lockfile)
- GUI subscribes to status via API endpoints: GET /api/embeddings/artifacts

## MongoDB collections and C# POCOs

Phantom.Workspaces uses MongoDB. Replace relational DDL with collection documents. Use MongoDB change streams for multi-instance notifications and MongoDB Atlas vector search (or store arrays and use approximate nearest neighbor libs).

Suggested collections:
- embeddings_configs
- model_artifacts
- reindex_jobs
- document_vectors

Example document shapes (JSON/BSON) and recommended C# POCOs using MongoDB.Bson attributes:

```csharp
public class EmbeddingsConfig
{
    [BsonId]
    public Guid Id { get; set; }

    [BsonElement("name")]
    public string? Name { get; set; }

    [BsonElement("scope")]
    public string Scope { get; set; } = "global"; // 'global'|'workspace'|'user'

    [BsonElement("isDefault")]
    public bool IsDefault { get; set; }

    [BsonElement("providerType")]
    public string ProviderType { get; set; } = "local"; // 'local'|'openai'|'azure'|'model-server'|'custom'

    [BsonElement("modelId")]
    public string? ModelId { get; set; }

    [BsonElement("modelArtifactId")]
    public Guid? ModelArtifactId { get; set; }

    [BsonElement("providerOptions")]
    public BsonDocument? ProviderOptions { get; set; }

    [BsonElement("batchSize")]
    public int BatchSize { get; set; } = 32;

    [BsonElement("cacheEnabled")]
    public bool CacheEnabled { get; set; } = true;

    [BsonElement("cacheTtlSeconds")]
    public int CacheTtlSeconds { get; set; } = 86400;

    [BsonElement("secretsRef")]
    public string? SecretsRef { get; set; }

    [BsonElement("version")]
    public int Version { get; set; } = 1;

    [BsonElement("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [BsonElement("updatedBy")]
    public string? UpdatedBy { get; set; }
}

public class ModelArtifact
{
    [BsonId]
    public Guid Id { get; set; }

    [BsonElement("embeddingsConfigId")]
    public Guid? EmbeddingsConfigId { get; set; }

    [BsonElement("artifactPath")]
    public string? ArtifactPath { get; set; }

    [BsonElement("downloadUrl")]
    public string? DownloadUrl { get; set; }

    [BsonElement("checksum")]
    public string? Checksum { get; set; }

    [BsonElement("sizeBytes")]
    public long? SizeBytes { get; set; }

    [BsonElement("provider")]
    public string? Provider { get; set; }

    [BsonElement("status")]
    public string Status { get; set; } = "queued"; // 'queued'|'downloading'|'ready'|'failed'

    [BsonElement("lastError")]
    public string? LastError { get; set; }

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [BsonElement("readyAt")]
    public DateTime? ReadyAt { get; set; }
}

public class ReindexJob
{
    [BsonId]
    public Guid Id { get; set; }

    [BsonElement("embeddingsConfigId")]
    public Guid EmbeddingsConfigId { get; set; }

    [BsonElement("scope")]
    public string Scope { get; set; } = "global";

    [BsonElement("status")]
    public string Status { get; set; } = "pending"; // 'pending'|'running'|'completed'|'failed'

    [BsonElement("progressPct")]
    public int ProgressPct { get; set; }

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [BsonElement("startedAt")]
    public DateTime? StartedAt { get; set; }

    [BsonElement("completedAt")]
    public DateTime? CompletedAt { get; set; }

    [BsonElement("lastError")]
    public string? LastError { get; set; }
}

// Document vector example using MongoDB Atlas Vector Search (or plain arrays if Atlas not available)
public class DocumentVector
{
    [BsonId]
    public Guid Id { get; set; }

    [BsonElement("documentId")]
    public Guid DocumentId { get; set; }

    [BsonElement("providerConfigId")]
    public Guid ProviderConfigId { get; set; }

    // For Atlas Vector Search use BsonArray of floats and create a vector index; otherwise store float[]
    [BsonElement("vector")]
    public float[] Vector { get; set; } = Array.Empty<float>();

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
```

Indexes and vector search
- Create indexes:
  - embeddings_configs: index on scope and isDefault
  - model_artifacts: index on embeddingsConfigId and status
  - reindex_jobs: index on status and createdAt
  - document_vectors: index on providerConfigId and vector (if using Atlas vector search create a vector index)

If using MongoDB Atlas Vector Search, create a vector index with k-NN options; if using self-hosted MongoDB (no Atlas vector), store vectors as float[] and run nearest-neighbor via an external ANN store (FAISS) or by approximate methods.

## System config key and "embeddings-config" JSON schema

- Store the authoritative embeddings configuration at the hierarchical system config key: ["system","config","embeddings"]. This centralizes multi-instance configuration, allows admin UIs to edit the single source of truth, and supports optimistic updates.

- The runtime provides an ISystemConfigStore abstraction (see above) that reads/writes a JSON blob at that key. The JSON must validate against the "embeddings-config" JSON schema below.

Example JSON schema (Draft):

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "title": "embeddings-config",
  "type": "object",
  "properties": {
    "id": { "type": "string", "format": "uuid" },
    "name": { "type": "string" },
    "scope": { "type": "string", "enum": ["global","workspace","user"] },
    "providerType": { "type": "string", "enum": ["local","openai","azure","model-server","custom"] },
    "modelId": { "type": ["string", "null"] },
    "modelArtifactId": { "type": ["string", "null"], "format": "uuid" },
    "providerOptions": { "type": ["object", "null"] },
    "batchSize": { "type": "integer", "minimum": 1, "default": 32 },
    "cacheEnabled": { "type": "boolean", "default": true },
    "cacheTtlSeconds": { "type": "integer", "minimum": 0, "default": 86400 },
    "secretsRef": { "type": ["string", "null"] },
    "version": { "type": "integer" }
  },
  "required": ["id","providerType","version"]
}
```

Notes:
- The system config store stores the validated JSON blob; managers should use TryUpdate semantics to perform optimistic-versioned updates and avoid races.
- The EmbeddingsConfigManager (IEmbeddingsConfigManager) is responsible for validating the JSON against the schema, sequencing ModelArtifact creation/fetch, and enqueueing ReindexJobs; it writes the committed config back to the system config key when the orchestration completes (or earlier, as a staged config with an orchestration token).

Change propagation
- Use MongoDB change streams to publish changes to EmbeddingsConfig and ModelArtifact collections. EmbeddingService instances subscribe to change streams to invalidate caches and react to artifact.ready events.

Sample change stream usage (C#):

```csharp
var pipeline = new EmptyPipelineDefinition<ChangeStreamDocument<BsonDocument>>().Match(change => change.OperationType == ChangeStreamOperationType.Update);
var options = new ChangeStreamOptions { FullDocument = ChangeStreamFullDocumentOption.UpdateLookup };
using var cursor = await _mongoDatabase.GetCollection<BsonDocument>("embeddings_configs").WatchAsync(pipeline, options, cancellationToken: ct);
await cursor.ForEachAsync(change => HandleConfigChange(change.FullDocument), ct);
```

Provider-specific notes: OpenAI
- EmbeddingsConfig example (OpenAI document):
  - provider_type = 'openai'
  - model_id = 'text-embedding-3-small'
  - secrets_ref = 'secret:openai_api_key'
  - provider_options = { "api_base": "https://api.openai.com/v1", "max_batch_tokens": 8192 }

- OpenAI provider implementation must:
  - Resolve secrets via secret store at runtime
  - Batch requests and respect token limits
  - Apply retries/backoff on 429/5xx
  - Emit token usage telemetry (no raw inputs)
  - Honor configured spend caps; fallback to local provider when limits exceeded

## APIs to expose
- GET /api/system/config/embeddings -> read the stored embeddings-config (from ["system","config","embeddings"]) and current orchestration status
- POST /api/system/config/embeddings -> create or update the embeddings-config JSON (validates against schema)
- POST /api/system/config/embeddings/stage -> stage & begin an orchestrated switch (returns orchestrationId)
- GET /api/system/config/embeddings/orchestration/{id} -> query orchestration status (download + reindex progress)
- GET /api/embeddings/status -> provider + artifact statuses, GPU status (runtime read-only)
- POST /api/embeddings/reindex -> enqueue an ad-hoc reindex job (admin)
- GET /api/embeddings/reindex/{id} -> job status

## Tasks
- Implement ISystemConfigStore (backed by MongoDB collection at system_config or equivalent) and JSON schema validation for "embeddings-config"
- Implement EmbeddingsConfigManager (IEmbeddingsConfigManager): validate, stage, orchestrate artifact fetch and reindex, provide orchestration status
- Implement repository layer for EmbeddingsConfig, ModelArtifact, ReindexJob and DocumentVector collections
- Implement IEmbeddingProvider concrete classes (Local/OpenAI/ModelServer) and provider factory that EmbeddingService uses at runtime
- Implement ModelFetcher and ModelArtifact lifecycle, including atomic materialization and multi-instance notifications
- Implement ReindexJob processing and EmbeddingsGenerator worker/tool
- Implement admin UI + diagnostics for system config, orchestrations, artifacts, and reindex progress
- Add unit & integration tests (validation, orchestration, provider fallbacks, multi-instance change propagation)
- Documentation: document system-config key usage, schema, and upgrade/migration guidance


Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
