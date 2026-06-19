using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Tools;

/// <summary>
/// A built-in scheduled tool that scans a directory tree for Git repositories (directories
/// containing a <c>.git</c> entry) and represents each as a <c>git</c> entity, so repositories
/// surface in the workspace. Entities are keyed by a stable, path-derived id, so re-running the
/// tool updates rather than duplicates. The scan does not descend into a repository once found, nor
/// into <c>.git</c> directories.
/// </summary>
public sealed class GitWorkspaceScanTool : IWorkspaceTool
{
    /// <summary>The tool-entity property naming the root directory to scan.</summary>
    public const string ScanRootProperty = "scan-root";

    /// <summary>The tool-entity property bounding how deep the scan descends. Defaults to 6.</summary>
    public const string MaxDepthProperty = "max-depth";

    private const int DefaultMaxDepth = 6;

    public string ToolType => "git-workspace-scan";

    public async Task<WorkspaceToolExecutionResult> ExecuteAsync(WorkspaceToolExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var scanRoot = ReadStringProperty(context.Tool.Data, ScanRootProperty);
        if (string.IsNullOrWhiteSpace(scanRoot) || !Directory.Exists(scanRoot))
        {
            return new WorkspaceToolExecutionResult();
        }

        var maxDepth = ReadIntProperty(context.Tool.Data, MaxDepthProperty) ?? DefaultMaxDepth;

        var changes = new List<EntityChange>();
        foreach (var repositoryPath in EnumerateGitRepositories(scanRoot, maxDepth, context.CancellationToken))
        {
            using var document = JsonDocument.Parse(BuildGitEntityJson(repositoryPath));
            changes.Add(new EntityChange
            {
                EntityId = new EntityId(DeterministicId(repositoryPath)),
                ConcurrencyTag = null,
                Data = document.RootElement.Clone(),
                EntityChangeMode = EntityChangeMode.Replace,
            });
        }

        if (changes.Count == 0)
        {
            return new WorkspaceToolExecutionResult();
        }

        await context.DataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "Scan for Git repositories." } },
                Changes = changes,
            },
            context.CancellationToken).ConfigureAwait(false);

        return new WorkspaceToolExecutionResult();
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
}
