using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Services.Access;

public sealed class ReleaseMemoryStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public ReleaseMemoryStore(string? filePath = null)
    {
        this.FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Phantom.Workspaces",
            "release-memory.json");
    }

    public string FilePath { get; }

    internal async ValueTask<IReadOnlyList<ReleaseMemoryRecord>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(this.FilePath))
        {
            return Array.Empty<ReleaseMemoryRecord>();
        }

        await using var stream = new FileStream(this.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var document = await JsonSerializer.DeserializeAsync<ReleaseMemoryDocument>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
        if (document is null || document.Version != 1)
        {
            throw new JsonException("Unsupported release memory document.");
        }

        return document.Records;
    }

    internal async ValueTask SaveAsync(
        IEnumerable<ReleaseMemoryRecord> records,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(this.FilePath))!;
        Directory.CreateDirectory(directory);
        var stagedPath = Path.Combine(directory, $".release-memory-{Guid.NewGuid():N}.pending");
        try
        {
            await using (var stream = new FileStream(
                stagedPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new ReleaseMemoryDocument { Version = 1, Records = records.ToArray() },
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stagedPath, this.FilePath, overwrite: true);
        }
        finally
        {
            File.Delete(stagedPath);
        }
    }

    private sealed record ReleaseMemoryDocument
    {
        public required int Version { get; init; }
        public required IReadOnlyList<ReleaseMemoryRecord> Records { get; init; }
    }
}

internal sealed record ReleaseMemoryRecord
{
    public required ReleaseMemoryIdentifier Identifier { get; init; }
    public required LogicalCredentialIdentifier LogicalIdentifier { get; init; }
    public required CredentialSourceIdentifier SourceIdentifier { get; init; }
    public CredentialAccountIdentifier? AccountIdentifier { get; init; }
    public required ReleaseMemoryScopeKind Scope { get; init; }
    public required CredentialUseFingerprint BindingFingerprint { get; init; }
}
