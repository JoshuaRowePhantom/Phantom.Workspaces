using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Phantom.Workspaces.Data.MongoDB;

public sealed class MongoDbChatHistoryProvider : ChatHistoryProvider
{
    private readonly IMongoCollection<MongoChatHistoryMessageDocument> _collection;
    private readonly string _stateKey;

    public MongoDbChatHistoryProvider(
        IMongoDatabase database,
        string collectionName,
        string? stateKey = null)
        : base(null, null, null)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (string.IsNullOrWhiteSpace(collectionName))
        {
            throw new ArgumentException("Collection name is required.", nameof(collectionName));
        }

        _collection = database.GetCollection<MongoChatHistoryMessageDocument>(collectionName);
        _stateKey = stateKey ?? collectionName;
    }

    public override IReadOnlyList<string> StateKeys => [_stateKey];

    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        ChatHistoryProvider.InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.Session);

        var sessionKey = GetSessionKey(context.Session);
        var documents = await _collection
            .Find(Builders<MongoChatHistoryMessageDocument>.Filter.Eq(static item => item.SessionKey, sessionKey))
            .SortBy(static item => item.Sequence)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var history = documents.Select(ToChatMessage).ToArray();

        var requestMessages = context.RequestMessages.ToArray();
        if (requestMessages.Length > 0)
        {
            await UpsertHistoryAsync(
                sessionKey,
                requestMessages.Select(ToStoredMessage),
                cancellationToken).ConfigureAwait(false);
        }

        return history;
    }

    protected override async ValueTask StoreChatHistoryAsync(
        ChatHistoryProvider.InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.Session);

        var sessionKey = GetSessionKey(context.Session);
        var responseMessages = context.ResponseMessages?.ToArray() ?? [];
        if (responseMessages.Length == 0)
        {
            return;
        }

        await UpsertHistoryAsync(
            sessionKey,
            responseMessages.Select(ToStoredMessage),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task UpsertHistoryAsync(
        string sessionKey,
        IEnumerable<MongoChatHistoryMessageDocument> messages,
        CancellationToken cancellationToken)
    {
        var nextSequence = await GetNextSequenceAsync(sessionKey, cancellationToken).ConfigureAwait(false);
        var documents = messages.Select((message, index) => message with
        {
            SessionKey = sessionKey,
            Sequence = nextSequence + index,
        }).ToArray();

        if (documents.Length == 0)
        {
            return;
        }

        await _collection.InsertManyAsync(
            documents,
            new InsertManyOptions { IsOrdered = true },
            cancellationToken)
            .ConfigureAwait(false);
    }

    private static string GetSessionKey(AgentSession session)
    {
        return $"session:{RuntimeHelpers.GetHashCode(session)}";
    }

    private static MongoChatHistoryMessageDocument ToStoredMessage(ChatMessage message)
    {
        return new MongoChatHistoryMessageDocument
        {
            Payload = BsonDocument.Parse(JsonSerializer.Serialize(message, AIJsonUtilities.DefaultOptions)),
        };
    }

    private static ChatMessage ToChatMessage(MongoChatHistoryMessageDocument document)
    {
        return JsonSerializer.Deserialize<ChatMessage>(document.Payload.ToJson(), AIJsonUtilities.DefaultOptions)
            ?? throw new InvalidOperationException("ChatMessage payload could not be deserialized.");
    }

    private async Task<long> GetNextSequenceAsync(
        string sessionKey,
        CancellationToken cancellationToken)
    {
        var lastMessage = await _collection
            .Find(Builders<MongoChatHistoryMessageDocument>.Filter.Eq(static item => item.SessionKey, sessionKey))
            .SortByDescending(static item => item.Sequence)
            .Limit(1)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return lastMessage is null ? 0 : lastMessage.Sequence + 1;
    }

    private sealed record MongoChatHistoryMessageDocument
    {
        [BsonId]
        public ObjectId Id { get; init; }

        public string SessionKey { get; init; } = string.Empty;

        public long Sequence { get; init; }

        public BsonDocument Payload { get; init; } = new();
    }
}
