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

    public MongoDbAgentPersistenceStore(
        IMongoDatabase database,
        string collectionName)
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
                UpdatedUtc = DateTime.UtcNow,
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
                UpdatedUtc = DateTime.UtcNow,
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

        return new PersistedAgent
        {
            AgentSessionId = sessionDocument.AgentSessionId,
            AgentSessionJson = sessionDocument.AgentSessionJson,
            AgentDefinitionJson = definitionDocument?.AgentDefinitionJson,
            CopilotSdkSessionId = sessionDocument.CopilotSdkSessionId,
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

    public async ValueTask<SubAgentManifestEntry[]> ReadSubAgentManifestAsync(
        string parentSessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentSessionId);

        var documents = await this.subAgentManifestCollection
            .Find(Builders<MongoDbSubAgentManifestDocument>.Filter.Eq(static x => x.ParentSessionId, parentSessionId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return documents
            .Select(static doc => new SubAgentManifestEntry
            {
                SessionId = doc.ChildSessionId,
                AgentDefinitionJson = doc.AgentDefinitionJson,
                CompletionState = doc.CompletionState,
                LastUpdatedAt = doc.LastUpdatedAt,
            })
            .ToArray();
    }

    public async ValueTask WriteSubAgentManifestEntryAsync(
        string parentSessionId,
        SubAgentManifestEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.SessionId);

        var document = new MongoDbSubAgentManifestDocument
        {
            Id = $"{parentSessionId}/{entry.SessionId}",
            ParentSessionId = parentSessionId,
            ChildSessionId = entry.SessionId,
            AgentDefinitionJson = entry.AgentDefinitionJson,
            CompletionState = entry.CompletionState,
            LastUpdatedAt = entry.LastUpdatedAt,
        };

        await this.subAgentManifestCollection.ReplaceOneAsync(
                filter: Builders<MongoDbSubAgentManifestDocument>.Filter.And(
                    Builders<MongoDbSubAgentManifestDocument>.Filter.Eq(static x => x.ParentSessionId, parentSessionId),
                    Builders<MongoDbSubAgentManifestDocument>.Filter.Eq(static x => x.ChildSessionId, entry.SessionId)),
                replacement: document,
                options: new ReplaceOptions { IsUpsert = true },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
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

        public BsonDocument AgentDefinitionJson { get; init; } = new();

        public AgentChatCompletionState CompletionState { get; init; }

        public DateTime LastUpdatedAt { get; init; }
    }
}
