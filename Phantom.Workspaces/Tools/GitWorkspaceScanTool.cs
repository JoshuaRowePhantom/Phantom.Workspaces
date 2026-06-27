using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Tools;

/// <summary>
/// A built-in scheduled tool that scans a directory tree for Git repositories (directories
/// containing a <c>.git</c> entry) and represents each as a <c>git</c> entity, so repositories
/// surface in the workspace. Entities are keyed by a stable, path-derived id, so re-running the
/// tool updates rather than duplicates. The scan does not descend into a repository once found, nor
/// into <c>.git</c> directories.
///
/// Out of the box (no configuration), it scans **all local fixed drives**, so enabling the tool just
/// works. The scan can be narrowed by setting top-level <c>scan-root</c> (a single path) or
/// <c>scan-roots</c> (an array of paths) on the tool entity, and bounded by <c>max-depth</c>.
/// </summary>
public sealed class GitWorkspaceScanTool : IWorkspaceTool
{
    /// <summary>The tool-entity property naming a single root directory to scan.</summary>
    public const string ScanRootProperty = "scan-root";

    /// <summary>The tool-entity property naming multiple root directories to scan.</summary>
    public const string ScanRootsProperty = "scan-roots";

    /// <summary>The tool-entity property bounding how deep the scan descends. Defaults to 6.</summary>
    public const string MaxDepthProperty = "max-depth";

    private const int DefaultMaxDepth = 6;

    private readonly ILogger<GitWorkspaceScanTool> logger;
    private readonly Func<IEnumerable<string>> localFixedDriveRootsProvider;

    /// <param name="localFixedDriveRootsProvider">
    /// Supplies the local fixed-drive root paths scanned by default when no <c>scan-root</c>/<c>scan-roots</c>
    /// is configured. Defaults to the machine's ready fixed drives; overridable for testing.
    /// </param>
    /// <param name="logger">Logger for this tool; defaults to <see cref="NullLogger{T}.Instance"/>.</param>
    public GitWorkspaceScanTool(
        Func<IEnumerable<string>>? localFixedDriveRootsProvider = null,
        ILogger<GitWorkspaceScanTool>? logger = null)
    {
        this.localFixedDriveRootsProvider = localFixedDriveRootsProvider ?? GetLocalFixedDriveRoots;
        this.logger = logger ?? NullLogger<GitWorkspaceScanTool>.Instance;
    }

    public string ToolType => "git-workspace-scan";

    public async Task<WorkspaceToolExecutionResult> ExecuteAsync(WorkspaceToolExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var scanRoots = this.ResolveScanRoots(context.Tool.Data);
        if (scanRoots.Count == 0)
        {
            return new WorkspaceToolExecutionResult
            {
                ResultContent = "No scan roots resolved; no repositories scanned.",
            };
        }

        var maxDepth = ReadIntProperty(context.Tool.Data, MaxDepthProperty) ?? DefaultMaxDepth;

        var changes = new List<EntityChange>();
        var seenRepositoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scanRoot in scanRoots)
        {
            foreach (var repositoryPath in EnumerateGitRepositories(scanRoot, maxDepth, context.CancellationToken))
            {
                if (!seenRepositoryPaths.Add(repositoryPath))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(BuildGitEntityJson(repositoryPath));
                changes.Add(new EntityChange
                {
                    EntityId = new EntityId(DeterministicId(repositoryPath)),
                    ConcurrencyTag = null,
                    Data = document.RootElement.Clone(),
                    EntityChangeMode = EntityChangeMode.Replace,
                });
            }
        }

        var repositoriesFound = seenRepositoryPaths.Count;
        var rootsDescription = string.Join(", ", scanRoots);

        if (changes.Count > 0)
        {
            await context.DataAccessLayer.UpdateAsync(
                new UpdateRequest
                {
                    UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "Scan for Git repositories." } },
                    Changes = changes,
                },
                context.CancellationToken).ConfigureAwait(false);
        }

        var summary = $"Scanned {scanRoots.Count} root(s) [{rootsDescription}]. Found {repositoriesFound} repositories.";
        return new WorkspaceToolExecutionResult { ResultContent = summary };
    }

    /// <summary>
    /// Resolves the directories to scan: explicit <c>scan-roots</c>/<c>scan-root</c> when configured,
    /// otherwise all local fixed drives. Only existing directories are returned.
    /// </summary>
    private IReadOnlyList<string> ResolveScanRoots(JsonElement? toolEntity)
    {
        var configuredRoots = ReadStringArrayProperty(toolEntity, ScanRootsProperty);
        if (ReadStringProperty(toolEntity, ScanRootProperty) is { Length: > 0 } singleRoot)
        {
            configuredRoots = [.. configuredRoots, singleRoot];
        }

        var existingConfiguredRoots = configuredRoots
            .Where(root => !string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (existingConfiguredRoots.Count > 0)
        {
            return existingConfiguredRoots;
        }

        IEnumerable<string> driveRoots;
        try
        {
            driveRoots = this.localFixedDriveRootsProvider();
        }
        catch (IOException ex)
        {
            this.logger.LogWarning(ex, "Failed to enumerate fixed drives; scan may be incomplete.");
            return [];
        }

        return driveRoots
            .Where(root => !string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> GetLocalFixedDriveRoots()
    {
        return DriveInfo.GetDrives()
            .Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady)
            .Select(drive => drive.RootDirectory.FullName)
            .ToList();
    }

    private static IEnumerable<string> EnumerateGitRepositories(string root, int maxDepth, CancellationToken cancellationToken)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((Path.GetFullPath(root), 0));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (path, depth) = pending.Pop();

            if (IsGitRepository(path))
            {
                // A repository is a leaf for scanning purposes; do not descend into it.
                yield return path;
                continue;
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            string[] subdirectories;
            try
            {
                subdirectories = Directory.GetDirectories(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var subdirectory in subdirectories)
            {
                var name = Path.GetFileName(subdirectory);
                if (string.Equals(name, ".git", StringComparison.Ordinal))
                {
                    continue;
                }

                pending.Push((subdirectory, depth + 1));
            }
        }
    }

    private static bool IsGitRepository(string path)
    {
        // A working tree has a .git directory; a bare repo / worktree may have a .git file.
        return Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git"));
    }

    private static Guid DeterministicId(string repositoryPath)
    {
        var normalized = Path.GetFullPath(repositoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("git-workspace-scan:" + normalized.ToLowerInvariant()));
        return new Guid(bytes);
    }

    private static string BuildGitEntityJson(string repositoryPath)
    {
        var fullPath = Path.GetFullPath(repositoryPath);
        var name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("entity-id", DeterministicId(repositoryPath).ToString());

            writer.WritePropertyName("entity-types");
            writer.WriteStartArray();
            writer.WriteStringValue("entity");
            writer.WriteStringValue("git");
            writer.WriteEndArray();

            writer.WritePropertyName("names");
            writer.WriteStartArray();
            writer.WriteStartArray();
            writer.WriteStringValue("git");
            writer.WriteStringValue(fullPath);
            writer.WriteEndArray();
            writer.WriteEndArray();

            writer.WritePropertyName("display-name");
            writer.WriteStartObject();
            writer.WriteString("default", name);
            writer.WriteEndObject();

            writer.WriteString("path", fullPath);

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? ReadStringProperty(JsonElement? toolEntity, string propertyName)
    {
        if (toolEntity is JsonElement toolEntityValue
            && toolEntityValue.ValueKind == JsonValueKind.Object
            && toolEntityValue.TryGetProperty(propertyName, out var element)
            && element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }

        return null;
    }

    private static int? ReadIntProperty(JsonElement? toolEntity, string propertyName)
    {
        if (toolEntity is JsonElement toolEntityValue
            && toolEntityValue.ValueKind == JsonValueKind.Object
            && toolEntityValue.TryGetProperty(propertyName, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out var value))
        {
            return value;
        }

        return null;
    }

    private static IReadOnlyList<string> ReadStringArrayProperty(JsonElement? toolEntity, string propertyName)
    {
        if (toolEntity is JsonElement toolEntityValue
            && toolEntityValue.ValueKind == JsonValueKind.Object
            && toolEntityValue.TryGetProperty(propertyName, out var element)
            && element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToList();
        }

        return [];
    }
}
