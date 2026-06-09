namespace Phantom.Workspaces.Llm;

public interface IFilesystemEditStore
{
    Task<string> StoreEditAsync(
        string path,
        string? originalContent,
        string? modifiedContent,
        bool? preview,
        string operation,
        CancellationToken cancellationToken = default);

    Task<StoredEdit?> GetEditAsync(
        string editId,
        CancellationToken cancellationToken = default);
}

public sealed record StoredEdit(
    string Id,
    string Path,
    string? OriginalContent,
    string? ModifiedContent,
    bool? Preview,
    string Operation,
    DateTime CreatedAt,
    string? SessionId = null,
    string? ToolCallId = null);
