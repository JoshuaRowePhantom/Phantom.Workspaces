using MongoDB.Bson;

namespace Phantom.Workspaces.Llm;

public sealed class InMemoryFilesystemEditStore : IFilesystemEditStore
{
    private readonly Dictionary<string, StoredEdit> edits = new(StringComparer.Ordinal);

    public Task<string> StoreEditAsync(
        string path,
        string? originalContent,
        string? modifiedContent,
        bool? preview,
        string operation,
        CancellationToken cancellationToken = default)
    {
        var editId = ObjectId.GenerateNewId().ToString();
        this.edits[editId] = new StoredEdit(
            Id: editId,
            Path: path,
            OriginalContent: originalContent,
            ModifiedContent: modifiedContent,
            Preview: preview,
            Operation: operation,
            CreatedAt: DateTime.UtcNow);
        return Task.FromResult(editId);
    }

    public Task<StoredEdit?> GetEditAsync(
        string editId,
        CancellationToken cancellationToken = default)
    {
        this.edits.TryGetValue(editId, out var edit);
        return Task.FromResult(edit);
    }
}
