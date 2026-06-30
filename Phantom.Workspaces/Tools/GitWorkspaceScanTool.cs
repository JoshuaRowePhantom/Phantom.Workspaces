using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    // Registered as "git-workspace-scan" — matches the seeded tool entity (git-workspace-scan-tool.json)
    // and the tool-relationship schema. Do not confuse with the separate "git-workspace-discovery" tool.
    public string ToolType => "git-workspace-scan";

    public async Task<WorkspaceToolExecutionResult> ExecuteAsync(WorkspaceToolExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var scanRoots = this.ResolveScanRoots(context.Tool.Data);
        if (scanRoots.Count == 0)
        {
            this.logger.LogWarning("Git workspace scan: no scan roots resolved; no repositories will be scanned.");
            return new WorkspaceToolExecutionResult
            {
                ResultContent = "No scan roots resolved; no repositories scanned.",
            };
        }

        var maxDepth = ReadIntProperty(context.Tool.Data, MaxDepthProperty) ?? DefaultMaxDepth;

        var discoveredEntities = new List<(EntityId EntityId, JsonElement Data)>();
        var seenRepositoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scanRoot in scanRoots)
        {
            this.logger.LogInformation("Scanning top-level directory: {Path}", scanRoot);
            var rootRepoCount = 0;
            foreach (var repositoryPath in this.EnumerateGitRepositories(scanRoot, maxDepth, context.CancellationToken))
            {
                if (!seenRepositoryPaths.Add(repositoryPath))
                {
                    continue;
                }

                rootRepoCount++;
                var entityId = DeterministicEntityId.Create("git-workspace-scan", NormalizeRepositoryPath(repositoryPath));
                var profileNames = WorkspaceEntitySnapshotReader.GetEntityNames(context.CurrentComputerUserProfileEntity);
                using var document = JsonDocument.Parse(BuildGitEntityJson(repositoryPath, profileNames));
                discoveredEntities.Add((entityId, document.RootElement.Clone()));
            }

            this.logger.LogInformation("Found {Count} git repositories in {Path}", rootRepoCount, scanRoot);
        }

        var repositoriesFound = discoveredEntities.Count;
        var rootsDescription = string.Join(", ", scanRoots);

        if (repositoriesFound == 0)
        {
            this.logger.LogInformation(
                "Git workspace scan: no repositories found under {Roots} root(s) [{RootsDescription}].",
                scanRoots.Count,
                rootsDescription);

            return new WorkspaceToolExecutionResult
            {
                ResultContent = $"Scanned {scanRoots.Count} root(s) [{rootsDescription}]. No repositories found.",
            };
        }

        // Fetch existing entities to classify each discovered repository as added, changed, or unchanged.
        var existingEntities = new Dictionary<EntityId, EntitySnapshot>();
        var getResult = await context.DataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities = discoveredEntities.Select(e => new GetEntityRequest { EntityId = e.EntityId }).ToList(),
            },
            context.CancellationToken).ConfigureAwait(false);

        foreach (var batch in getResult.Batches)
        {
            foreach (var entity in batch.Entities)
            {
                existingEntities[entity.EntityId] = entity;
            }
        }

        var changes = new List<EntityChange>();
        var added = 0;
        var changed = 0;
        var unchanged = 0;

        foreach (var (entityId, newData) in discoveredEntities)
        {
            if (!existingEntities.TryGetValue(entityId, out var existing) || existing.Data is null)
            {
                added++;
                changes.Add(new EntityChange
                {
                    EntityId = entityId,
                    ConcurrencyTag = null,
                    Data = newData,
                    EntityChangeMode = EntityChangeMode.Replace,
                });
            }
            else if (JsonElement.DeepEquals(existing.Data.Value, newData))
            {
                unchanged++;
            }
            else
            {
                changed++;
                changes.Add(new EntityChange
                {
                    EntityId = entityId,
                    ConcurrencyTag = existing.ConcurrencyTag,
                    Data = newData,
                    EntityChangeMode = EntityChangeMode.Replace,
                });
            }
        }

        if (changes.Count > 0)
        {
            var updateResult = await context.DataAccessLayer.UpdateAsync(
                new UpdateRequest
                {
                    UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "Scan for Git repositories." } },
                    Changes = changes,
                },
                context.CancellationToken).ConfigureAwait(false);

            var failedCount = updateResult.EntityResults.Count(r => r.UpdateState == UpdateState.Failed);
            if (failedCount > 0)
            {
                this.logger.LogWarning(
                    "Git workspace scan: {Failed}/{Total} entity writes were rejected by the DAL.",
                    failedCount,
                    changes.Count);
            }
        }

        this.logger.LogInformation(
            "Git workspace scan: {Added} added, {Changed} changed, {Unchanged} unchanged across {Roots} root(s) [{RootsDescription}].",
            added,
            changed,
            unchanged,
            scanRoots.Count,
            rootsDescription);

        return new WorkspaceToolExecutionResult
        {
            ResultContent = $"Scanned {scanRoots.Count} root(s) [{rootsDescription}]. Found {repositoriesFound} repositories: added: {added}, changed: {changed}, unchanged: {unchanged}.",
        };
    }

    /// <summary>
    /// Resolves the directories to scan: explicit <c>scan-roots</c>/<c>scan-root</c> when configured,
    /// otherwise all local fixed drives. Only existing directories are returned.
    /// When roots are explicitly configured, they are always respected — missing paths are logged as
    /// warnings and skipped, but the tool never silently falls back to drive enumeration.
    /// </summary>
    private IReadOnlyList<string> ResolveScanRoots(JsonElement? toolEntity)
    {
        var configuredRoots = ReadStringArrayProperty(toolEntity, ScanRootsProperty);
        if (ReadStringProperty(toolEntity, ScanRootProperty) is { Length: > 0 } singleRoot)
        {
            configuredRoots = [.. configuredRoots, singleRoot];
        }

        if (configuredRoots.Count > 0)
        {
            // Configured roots are always respected; never substitute drive enumeration when roots are explicit.
            var existingRoots = new List<string>();
            foreach (var root in configuredRoots)
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                if (!Directory.Exists(root))
                {
                    this.logger.LogWarning(
                        "Configured scan root does not exist on disk and will be skipped: {Path}", root);
                    continue;
                }

                existingRoots.Add(Path.GetFullPath(root));
            }

            var distinctRoots = existingRoots
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (distinctRoots.Count == 0)
            {
                this.logger.LogWarning(
                    "All configured scan roots are absent on disk; no repositories will be scanned.");
            }

            return distinctRoots;
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

    private IEnumerable<string> EnumerateGitRepositories(string root, int maxDepth, CancellationToken cancellationToken)
        => GitRepositoryMetadataReader.EnumerateGitRepositories(root, maxDepth, cancellationToken, this.logger);

    private static string NormalizeRepositoryPath(string repositoryPath)
    {
        return Path.GetFullPath(repositoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
    }

    private static string BuildGitEntityJson(string repositoryPath, IReadOnlyCollection<EntityName> profileNames)
    {
        var fullPath = Path.GetFullPath(repositoryPath);
        var name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("entity-id", DeterministicEntityId.Create("git-workspace-scan", NormalizeRepositoryPath(repositoryPath)).ToString());

            writer.WritePropertyName("entity-types");
            writer.WriteStartArray();
            writer.WriteStringValue("entity");
            writer.WriteStringValue("git-worktree");
            writer.WriteStringValue("filesystem-path");
            writer.WriteEndArray();

            writer.WritePropertyName("names");
            writer.WriteStartArray();

            // Primary name
            writer.WriteStartArray();
            writer.WriteStringValue("git-worktrees");
            writer.WriteStringValue(fullPath);
            writer.WriteEndArray();

            // Secondary profile name -- enables GitWorkspacesViewModel to group by profile
            var profileName = profileNames.FirstOrDefault(
                n => n.Components.Length > 0
                     && string.Equals(n.Components[0], "computer-user-profiles", StringComparison.Ordinal));
            if (profileName.Components.Length > 0)
            {
                writer.WriteStartArray();
                foreach (var component in profileName.Components)
                {
                    writer.WriteStringValue(component);
                }

                writer.WriteEndArray();
            }

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

