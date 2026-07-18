using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Data.MongoDB;

public sealed class MongoDbFilesystemEditStore : IFilesystemEditStore
{
    private readonly IMongoCollection<MongoDbFilesystemEditDocument> edits;
    private readonly TimeProvider timeProvider;

    public MongoDbFilesystemEditStore(
        IMongoCollection<MongoDbFilesystemEditDocument> edits,
        TimeProvider? timeProvider = null)
    {
        this.edits = edits ?? throw new ArgumentNullException(nameof(edits));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<string> StoreEditAsync(
        string path,
        string? originalContent,
        string? modifiedContent,
        bool? preview,
        string operation,
        CancellationToken cancellationToken = default)
    {
        var document = new MongoDbFilesystemEditDocument
        {
            Id = ObjectId.GenerateNewId(),
            Path = path,
            OriginalContent = originalContent,
            ModifiedContent = modifiedContent,
            Preview = preview,
            Operation = operation,
            CreatedAt = this.timeProvider.GetUtcNow().UtcDateTime,
        };

        await this.edits.InsertOneAsync(document, cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.Id.ToString();
    }

    public async Task<StoredEdit?> GetEditAsync(
        string editId,
        CancellationToken cancellationToken = default)
    {
        if (!ObjectId.TryParse(editId, out var objectId))
        {
            return null;
        }

        var document = await this.edits
            .Find(Builders<MongoDbFilesystemEditDocument>.Filter.Eq(item => item.Id, objectId))
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        return new StoredEdit(
            Id: document.Id.ToString(),
            Path: document.Path ?? string.Empty,
            OriginalContent: document.OriginalContent,
            ModifiedContent: document.ModifiedContent,
            Preview: document.Preview,
            Operation: document.Operation ?? string.Empty,
            CreatedAt: document.CreatedAt,
            SessionId: document.SessionId,
            ToolCallId: document.ToolCallId);
    }
}

[BsonIgnoreExtraElements]
public sealed class MongoDbFilesystemEditDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    public string? Path { get; set; }

    public string? OriginalContent { get; set; }

    public string? ModifiedContent { get; set; }

    public bool? Preview { get; set; }

    public string? Operation { get; set; }

    public DateTime CreatedAt { get; set; }

    public string? SessionId { get; set; }

    public string? ToolCallId { get; set; }
}
