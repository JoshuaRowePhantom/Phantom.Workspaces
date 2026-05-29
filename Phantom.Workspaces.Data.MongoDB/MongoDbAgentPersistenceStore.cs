using System.Text.Json;
using Microsoft.Extensions.AI;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Data.MongoDB;

public sealed class MongoDbAgentPersistenceStore : IAgentPersistenceStore
{
    private readonly IMongoCollection<MongoPersistedSessionDocument> sessionsCollection;
    private readonly IMongoCollection<MongoPersistedDefinitionDocument> definitionsCollection;
    private readonly IMongoCollection<MongoPersistedMessageDocument> messagesCollection;

    public MongoDbAgentPersistenceStore(
        IMongoDatabase database,
        string collectionName)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (string.IsNullOrWhiteSpace(collectionName))
        {
            throw new ArgumentException("Collection name is required.", nameof(collectionName));
        }

        this.sessionsCollection = database.GetCollection<MongoPersistedSessionDocument>($"{collectionName}-sessions");
        this.definitionsCollection = database.GetCollection<MongoPersistedDefinitionDocument>($"{collectionName}-definitions");
        this.messagesCollection = database.GetCollection<MongoPersistedMessageDocument>($"{collectionName}-messages");
    }

    public async ValueTask StoreAsync(StoreRequestAgent request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Agent.AgentSessionId);

        if (request.Agent.AgentSessionJson is not null)
        {
            var persistedSessionDocument = new MongoPersistedSessionDocument
            {
                AgentSessionId = request.Agent.AgentSessionId,
                AgentSessionJson = request.Agent.AgentSessionJson,
                UpdatedUtc = DateTime.UtcNow,
            };

            await this.sessionsCollection.ReplaceOneAsync(
                    filter: Builders<MongoPersistedSessionDocument>.Filter.Eq(static x => x.AgentSessionId, request.Agent.AgentSessionId),
                    replacement: persistedSessionDocument,
                    options: new ReplaceOptions { IsUpsert = true },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        if (request.Agent.AgentDefinitionJson is not null)
        {
            var persistedDefinitionDocument = new MongoPersistedDefinitionDocument
            {
                AgentSessionId = request.Agent.AgentSessionId,
                AgentDefinitionJson = request.Agent.AgentDefinitionJson,
                UpdatedUtc = DateTime.UtcNow,
            };

            await this.definitionsCollection.ReplaceOneAsync(
                    filter: Builders<MongoPersistedDefinitionDocument>.Filter.Eq(static x => x.AgentSessionId, request.Agent.AgentSessionId),
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
        var documents = newMessages.Select((message, index) => new MongoPersistedMessageDocument
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
            .Find(Builders<MongoPersistedSessionDocument>.Filter.Eq(static x => x.AgentSessionId, request.AgentSessionId))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (sessionDocument is null)
        {
            return null;
        }

        var definitionDocument = await this.definitionsCollection
            .Find(Builders<MongoPersistedDefinitionDocument>.Filter.Eq(static x => x.AgentSessionId, request.AgentSessionId))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PersistedAgent
        {
            AgentSessionId = sessionDocument.AgentSessionId,
            AgentSessionJson = sessionDocument.AgentSessionJson,
            AgentDefinitionJson = definitionDocument?.AgentDefinitionJson,
        };
    }

    public async ValueTask<ChatMessage[]> ReadMessagesAsync(
        ReadMessagesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AgentSessionId);

        var documents = await this.messagesCollection
            .Find(Builders<MongoPersistedMessageDocument>.Filter.Eq(static x => x.AgentSessionId, request.AgentSessionId))
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
            .Find(Builders<MongoPersistedMessageDocument>.Filter.Eq(static x => x.AgentSessionId, agentSessionId))
            .SortByDescending(static x => x.Sequence)
            .Limit(1)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return lastMessage is null ? 0 : lastMessage.Sequence + 1;
    }

    private sealed record MongoPersistedSessionDocument
    {
        [BsonId]
        public string AgentSessionId { get; init; } = string.Empty;

        public BsonDocument? AgentSessionJson { get; init; }

        public DateTime UpdatedUtc { get; init; }
    }

    private sealed record MongoPersistedDefinitionDocument
    {
        [BsonId]
        public string AgentSessionId { get; init; } = string.Empty;

        public BsonDocument? AgentDefinitionJson { get; init; }

        public DateTime UpdatedUtc { get; init; }
    }

    private sealed record MongoPersistedMessageDocument
    {
        [BsonId]
        public ObjectId Id { get; init; }

        public string AgentSessionId { get; init; } = string.Empty;

        public long Sequence { get; init; }

        public BsonDocument Payload { get; init; } = new();
    }
}
