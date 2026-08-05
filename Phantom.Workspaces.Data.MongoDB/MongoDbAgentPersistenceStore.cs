using System.Text.Json;
using Microsoft.Extensions.AI;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Data.MongoDB;

public sealed class MongoDbAgentPersistenceStore : IAgentPersistenceStore
{
    private readonly IMongoCollection<MongoDbPersistedSessionDocument> sessionsCollection;
    private readonly IMongoCollection<MongoDbPersistedDefinitionDocument> definitionsCollection;
    private readonly IMongoCollection<MongoDbPersistedMessageDocument> messagesCollection;
    private readonly IMongoCollection<MongoDbSubAgentManifestDocument> subAgentManifestCollection;
    private readonly TimeProvider timeProvider;

    public MongoDbAgentPersistenceStore(
        IMongoDatabase database,
        string collectionName,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (string.IsNullOrWhiteSpace(collectionName))
        {
            throw new ArgumentException("Collection name is required.", nameof(collectionName));
        }

        this.sessionsCollection = database.GetCollection<MongoDbPersistedSessionDocument>($"{collectionName}-sessions");
        this.definitionsCollection = database.GetCollection<MongoDbPersistedDefinitionDocument>($"{collectionName}-definitions");
        this.messagesCollection = database.GetCollection<MongoDbPersistedMessageDocument>($"{collectionName}-messages");
        this.subAgentManifestCollection = database.GetCollection<MongoDbSubAgentManifestDocument>($"{collectionName}-sub-agent-manifests");
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask StoreAsync(StoreRequestAgent request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Agent.AgentSessionId);

        if (request.Agent.AgentSessionJson is not null)
        {
            // Preserve a previously stored SDK session id when this store call does not carry one,
            // mirroring how the in-memory store coalesces (issue #3).
            var copilotSdkSessionId = request.Agent.CopilotSdkSessionId;
            if (copilotSdkSessionId is null)
            {
                var existingSessionDocument = await this.sessionsCollection
                    .Find(Builders<MongoDbPersistedSessionDocument>.Filter.Eq(static x => x.AgentSessionId, request.Agent.AgentSessionId))
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                copilotSdkSessionId = existingSessionDocument?.CopilotSdkSessionId;
            }

            var persistedSessionDocument = new MongoDbPersistedSessionDocument
            {
                AgentSessionId = request.Agent.AgentSessionId,
                AgentSessionJson = request.Agent.AgentSessionJson,
                CopilotSdkSessionId = copilotSdkSessionId,
                UpdatedUtc = this.timeProvider.GetUtcNow().UtcDateTime,
            };

            await this.sessionsCollection.ReplaceOneAsync(
                    filter: Builders<MongoDbPersistedSessionDocument>.Filter.Eq(static x => x.AgentSessionId, request.Agent.AgentSessionId),
                    replacement: persistedSessionDocument,
                    options: new ReplaceOptions { IsUpsert = true },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        if (request.Agent.AgentDefinitionJson is not null)
        {
            var persistedDefinitionDocument = new MongoDbPersistedDefinitionDocument
            {
                AgentSessionId = request.Agent.AgentSessionId,
                AgentDefinitionJson = request.Agent.AgentDefinitionJson,
                UpdatedUtc = this.timeProvider.GetUtcNow().UtcDateTime,
            };

            await this.definitionsCollection.ReplaceOneAsync(
                    filter: Builders<MongoDbPersistedDefinitionDocument>.Filter.Eq(static x => x.AgentSessionId, request.Agent.AgentSessionId),
                    replacement: persistedDefinitionDocument,
                    options: new ReplaceOptions { IsUpsert = true },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        var newMessages = request.NewMessages ?? [];
        if (newMessages.Length == 0)
        {
            return;
        }

        var nextSequence = await this.GetNextSequenceAsync(request.Agent.AgentSessionId, cancellationToken).ConfigureAwait(false);
        var documents = newMessages.Select((message, index) => new MongoDbPersistedMessageDocument
        {
            AgentSessionId = request.Agent.AgentSessionId,
            Sequence = nextSequence + index,
            Payload = BsonDocument.Parse(JsonSerializer.Serialize(message, AIJsonUtilities.DefaultOptions)),
        }).ToArray();

        await this.messagesCollection.InsertManyAsync(
                documents,
                new InsertManyOptions { IsOrdered = true },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<PersistedAgent?> RestoreAsync(
        RestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AgentSessionId);

        var sessionDocument = await this.sessionsCollection
            .Find(Builders<MongoDbPersistedSessionDocument>.Filter.Eq(static x => x.AgentSessionId, request.AgentSessionId))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (sessionDocument is null)
        {
            return null;
        }

        var definitionDocument = await this.definitionsCollection
            .Find(Builders<MongoDbPersistedDefinitionDocument>.Filter.Eq(static x => x.AgentSessionId, request.AgentSessionId))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var agentDefinitionJson = definitionDocument?.AgentDefinitionJson;
        if (agentDefinitionJson is null)
        {
            // Fix #1187: legacy hosted Copilot sub-agents were persisted before the router
            // wrote a full per-sub-agent AgentDefinition (or wrote only the two-field
            // synthetic {"kind":"prompt","model":{"provider":"github-copilot-subagent"}})
            // which round-tripped as null through PromptAgent. When we can prove this
            // session is a sub-agent (there is a manifest link pointing at it), substitute
            // the canonical full hosted-Copilot sub-agent definition so restore never
            // returns a null AgentDefinitionJson for hosted sub-agents.
            var subAgentManifestExists = await this.subAgentManifestCollection
                .Find(Builders<MongoDbSubAgentManifestDocument>.Filter.Eq(
                    static x => x.ChildSessionId,
                    request.AgentSessionId))
                .AnyAsync(cancellationToken)
                .ConfigureAwait(false);
            if (subAgentManifestExists)
            {
                agentDefinitionJson = CopilotSubAgentDefinitionDefaults.BuildBsonJson(
                    request.AgentSessionId);
            }
        }

        return new PersistedAgent
        {
            AgentSessionId = sessionDocument.AgentSessionId,
            AgentSessionJson = sessionDocument.AgentSessionJson,
            AgentDefinitionJson = agentDefinitionJson,
            CopilotSdkSessionId = sessionDocument.CopilotSdkSessionId,
            LastUpdatedUtc = sessionDocument.UpdatedUtc == default
                ? null
                : sessionDocument.UpdatedUtc,
        };
    }

    public async ValueTask<ChatMessage[]> ReadMessagesAsync(
        ReadMessagesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AgentSessionId);

        var documents = await this.messagesCollection
            .Find(Builders<MongoDbPersistedMessageDocument>.Filter.Eq(static x => x.AgentSessionId, request.AgentSessionId))
            .SortBy(static x => x.Sequence)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return documents
            .Select(static document =>
                JsonSerializer.Deserialize<ChatMessage>(document.Payload.ToJson(), AIJsonUtilities.DefaultOptions)
                ?? throw new InvalidOperationException("Stored chat message payload could not be deserialized."))
            .ToArray();
    }

    private async Task<long> GetNextSequenceAsync(string agentSessionId, CancellationToken cancellationToken)
    {
        var lastMessage = await this.messagesCollection
            .Find(Builders<MongoDbPersistedMessageDocument>.Filter.Eq(static x => x.AgentSessionId, agentSessionId))
            .SortByDescending(static x => x.Sequence)
            .Limit(1)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return lastMessage is null ? 0 : lastMessage.Sequence + 1;
    }

    public async ValueTask AddSubAgentLinkAsync(
        string parentSessionId,
        string childSessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(childSessionId);

        var document = new MongoDbSubAgentManifestDocument
        {
            Id = $"{parentSessionId}/{childSessionId}",
            ParentSessionId = parentSessionId,
            ChildSessionId = childSessionId,
        };

        await this.subAgentManifestCollection.ReplaceOneAsync(
                filter: Builders<MongoDbSubAgentManifestDocument>.Filter.Eq(static x => x.Id, document.Id),
                replacement: document,
                options: new ReplaceOptions { IsUpsert = true },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<AgentSessionId>> ReadSubAgentChildIdsAsync(
        string parentSessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentSessionId);

        var documents = await this.subAgentManifestCollection
            .Find(Builders<MongoDbSubAgentManifestDocument>.Filter.Eq(static x => x.ParentSessionId, parentSessionId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return documents.Select(static d => new AgentSessionId(d.ChildSessionId)).ToList();
    }

    private sealed record MongoDbPersistedSessionDocument
    {
        [BsonId]
        public string AgentSessionId { get; init; } = string.Empty;

        public BsonDocument? AgentSessionJson { get; init; }

        public string? CopilotSdkSessionId { get; init; }

        public DateTime UpdatedUtc { get; init; }
    }

    private sealed record MongoDbPersistedDefinitionDocument
    {
        [BsonId]
        public string AgentSessionId { get; init; } = string.Empty;

        public BsonDocument? AgentDefinitionJson { get; init; }

        public DateTime UpdatedUtc { get; init; }
    }

    private sealed record MongoDbPersistedMessageDocument
    {
        [BsonId]
        public ObjectId Id { get; init; }

        public string AgentSessionId { get; init; } = string.Empty;

        public long Sequence { get; init; }

        public BsonDocument Payload { get; init; } = new();
    }

    private sealed record MongoDbSubAgentManifestDocument
    {
        [BsonId]
        public string Id { get; init; } = string.Empty;

        public string ParentSessionId { get; init; } = string.Empty;

        public string ChildSessionId { get; init; } = string.Empty;
    }
}
