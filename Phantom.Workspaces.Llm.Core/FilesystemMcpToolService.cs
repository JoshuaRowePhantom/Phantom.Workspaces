using System.Text.Json;
using System.Text.RegularExpressions;

namespace Phantom.Workspaces.Llm;

public sealed class FilesystemMcpToolService
{
    private readonly IFilesystemEditStore editStore;

    public FilesystemMcpToolService(IFilesystemEditStore editStore)
    {
        this.editStore = editStore ?? throw new ArgumentNullException(nameof(editStore));
    }

    public ReadResult Read(string path, int? start = null, int? end = null)
    {
        if (!File.Exists(path))
        {
            return new ReadResult(success: false, error: $"File not found: {path}", content: null);
        }

        var lines = File.ReadAllLines(path);
        var startLine = start.HasValue ? Math.Max(1, start.Value) : 1;
        var endLine = end.HasValue ? Math.Min(lines.Length, end.Value) : lines.Length;

        if (startLine > lines.Length)
        {
            return new ReadResult(success: false, error: $"Start line {startLine} exceeds file length {lines.Length}", content: null);
        }

        var selectedLines = lines[(startLine - 1)..endLine];
        return new ReadResult(success: true, error: null, content: string.Join(Environment.NewLine, selectedLines));
    }

    public SearchResult Search(
        string path,
        string? pattern = null,
        string? text = null,
        bool listOnly = false,
        int? beforeContext = null,
        int? afterContext = null,
        int? context = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new SearchResult(success: false, matches: [], totalMatches: 0, error: "Path is required.");
        }

        var effectiveBeforeContext = context ?? beforeContext ?? 0;
        var effectiveAfterContext = context ?? afterContext ?? 0;

        if (File.Exists(path))
        {
            return SearchFiles(
                directory: Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory(),
                filePattern: Path.GetFileName(path),
                recursive: false,
                specificFilePaths: [path],
                pattern,
                text,
                listOnly,
                effectiveBeforeContext,
                effectiveAfterContext);
        }

        if (Directory.Exists(path))
        {
            return SearchFiles(
                directory: path,
                filePattern: "*",
                recursive: false,
                specificFilePaths: null,
                pattern,
                text,
                listOnly,
                effectiveBeforeContext,
                effectiveAfterContext);
        }

        if (!HasGlobWildcard(path))
        {
            return new SearchResult(success: false, matches: [], totalMatches: 0, error: $"Path not found: {path}");
        }

        var directory = ResolveGlobRootDirectory(path);
        if (!Directory.Exists(directory))
        {
            return new SearchResult(success: false, matches: [], totalMatches: 0, error: $"Directory not found: {directory}");
        }

        var filePattern = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(filePattern) || string.Equals(filePattern, "**", StringComparison.Ordinal))
        {
            filePattern = "*";
        }

        return SearchFiles(
            directory,
            filePattern,
            recursive: path.Contains("**", StringComparison.Ordinal),
            specificFilePaths: null,
            pattern,
            text,
            listOnly,
            effectiveBeforeContext,
            effectiveAfterContext);
    }

    public FilesystemOperationResult MakeDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new FilesystemOperationResult(success: false, error: "Path is required.");
        }

        Directory.CreateDirectory(path);
        return new FilesystemOperationResult(success: true, error: null);
    }

    public FilesystemOperationResult RemoveItem(string path, bool recurse = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new FilesystemOperationResult(success: false, error: "Path is required.");
        }

        if (File.Exists(path))
        {
            File.Delete(path);
            return new FilesystemOperationResult(success: true, error: null);
        }

        if (Directory.Exists(path))
        {
            try
            {
                Directory.Delete(path, recursive: recurse);
                return new FilesystemOperationResult(success: true, error: null);
            }
            catch (Exception exception)
            {
                return new FilesystemOperationResult(success: false, error: exception.Message);
            }
        }

        return new FilesystemOperationResult(success: false, error: $"Item not found: {path}");
    }

    public FilesystemOperationResult MoveItem(string sourcePath, string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destinationPath))
        {
            return new FilesystemOperationResult(success: false, error: "Source and destination paths are required.");
        }

        if (File.Exists(sourcePath))
        {
            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            File.Move(sourcePath, destinationPath);
            return new FilesystemOperationResult(success: true, error: null);
        }

        if (Directory.Exists(sourcePath))
        {
            Directory.Move(sourcePath, destinationPath);
            return new FilesystemOperationResult(success: true, error: null);
        }

        return new FilesystemOperationResult(success: false, error: $"Item not found: {sourcePath}");
    }

    private static SearchResult SearchFiles(
        string directory,
        string filePattern,
        bool recursive,
        IReadOnlyList<string>? specificFilePaths,
        string? pattern,
        string? text,
        bool listOnly,
        int effectiveBeforeContext,
        int effectiveAfterContext)
    {
        var matchingFiles = specificFilePaths ?? Directory.EnumerateFiles(
            directory,
            filePattern,
            recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).ToList();

        if (matchingFiles.Count == 0)
        {
            return new SearchResult(success: true, matches: [], totalMatches: 0);
        }

        var results = new List<SearchMatch>();
        foreach (var filePath in matchingFiles)
        {
            if (listOnly)
            {
                results.Add(new SearchMatch(path: filePath, line: null, lines: null));
                continue;
            }

            string[] fileLines;
            try
            {
                fileLines = File.ReadAllLines(filePath);
            }
            catch
            {
                continue;
            }

            for (var index = 0; index < fileLines.Length; index++)
            {
                var currentLine = fileLines[index];
                var isMatch = false;

                if (!string.IsNullOrEmpty(text))
                {
                    isMatch = currentLine.Contains(text, StringComparison.Ordinal);
                }
                else if (!string.IsNullOrEmpty(pattern))
                {
                    isMatch = Regex.IsMatch(currentLine, pattern);
                }

                if (!isMatch)
                {
                    continue;
                }

                var contextStart = Math.Max(0, index - effectiveBeforeContext);
                var contextEnd = Math.Min(fileLines.Length - 1, index + effectiveAfterContext);
                var contextLines = new Dictionary<int, string>();
                for (var contextIndex = contextStart; contextIndex <= contextEnd; contextIndex++)
                {
                    contextLines[contextIndex + 1] = fileLines[contextIndex];
                }

                results.Add(new SearchMatch(
                    path: filePath,
                    line: index + 1,
                    lines: contextLines.Count == 0 ? null : contextLines));
            }
        }

        return new SearchResult(success: true, matches: results, totalMatches: results.Count);
    }

    private static bool HasGlobWildcard(string path)
        => path.Contains('*') || path.Contains('?');

    private static string ResolveGlobRootDirectory(string path)
    {
        var wildcardIndex = path.IndexOfAny(['*', '?']);
        if (wildcardIndex < 0)
        {
            return Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory();
        }

        var separatorIndex = path.LastIndexOfAny(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            wildcardIndex);
        if (separatorIndex < 0)
        {
            return Directory.GetCurrentDirectory();
        }

        var rootDirectory = path[..separatorIndex];
        return string.IsNullOrWhiteSpace(rootDirectory)
            ? Directory.GetCurrentDirectory()
            : rootDirectory;
    }

    public async Task<EditResult> EditAsync(
        string path,
        string? searchText = null,
        string? searchRegex = null,
        string? replaceText = null,
        string? replaceRegex = null,
        bool preview = false,
        bool delete = false,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path) && !delete)
        {
            return new EditResult(success: false, error: $"File not found: {path}", editId: null);
        }

        if (delete)
        {
            var deleteOriginalContent = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            var deleteModifiedContent = string.Empty;
            if (!preview && File.Exists(path))
            {
                File.Delete(path);
            }

            var deleteEditId = await this.editStore.StoreEditAsync(
                path,
                deleteOriginalContent,
                deleteModifiedContent,
                preview,
                operation: "delete",
                cancellationToken);
            return new EditResult(success: true, error: null, editId: deleteEditId);
        }

        var originalContent = File.ReadAllText(path);
        var modifiedContent = originalContent;
        if (searchText is not null && replaceText is not null)
        {
            modifiedContent = modifiedContent.Replace(searchText, replaceText, StringComparison.Ordinal);
        }
        else if (searchRegex is not null && replaceRegex is not null)
        {
            modifiedContent = Regex.Replace(modifiedContent, searchRegex, replaceRegex);
        }
        else
        {
            return new EditResult(success: false, error: "Must provide either (searchText + replaceText) or (searchRegex + replaceRegex).", editId: null);
        }

        if (modifiedContent == originalContent)
        {
            return new EditResult(success: false, error: "Search input did not match any content.", editId: null);
        }

        if (!preview)
        {
            File.WriteAllText(path, modifiedContent);
        }

        var storedEditId = await this.editStore.StoreEditAsync(
            path,
            originalContent,
            modifiedContent,
            preview,
            operation: "replace",
            cancellationToken);
        return new EditResult(success: true, error: null, editId: storedEditId);
    }

    public ApplyEditsResult EditApply(string editsJson)
    {
        var request = JsonSerializer.Deserialize<ApplyEditsRequest>(editsJson)
            ?? throw new InvalidOperationException("Invalid edit-apply payload.");

        var appliedCount = 0;
        foreach (var fileEdit in request.Edits)
        {
            if (fileEdit.delete)
            {
                if (File.Exists(fileEdit.path))
                {
                    File.Delete(fileEdit.path);
                }

                appliedCount++;
                continue;
            }

            if (fileEdit.newLines is null || fileEdit.newLines.Count == 0)
            {
                continue;
            }

            var currentContent = File.Exists(fileEdit.path) ? File.ReadAllText(fileEdit.path) : string.Empty;
            var currentLines = currentContent.Split(new[] { Environment.NewLine }, StringSplitOptions.None).ToList();
            foreach (var lineEdit in fileEdit.newLines.OrderByDescending(pair => pair.Key))
            {
                var lineIndex = lineEdit.Key - 1;
                if (lineIndex >= 0 && lineIndex < currentLines.Count)
                {
                    currentLines[lineIndex] = lineEdit.Value;
                    continue;
                }

                if (lineIndex == currentLines.Count)
                {
                    currentLines.Add(lineEdit.Value);
                }
            }

            File.WriteAllText(fileEdit.path, string.Join(Environment.NewLine, currentLines));
            appliedCount++;
        }

        return new ApplyEditsResult(success: true, appliedCount: appliedCount, error: null);
    }

    public async Task<DescribeEditResult> DescribeEditAsync(string editId, CancellationToken cancellationToken = default)
    {
        var edit = await this.editStore.GetEditAsync(editId, cancellationToken);
        if (edit is null)
        {
            return new DescribeEditResult(success: false, error: $"Edit not found: {editId}", edits: null);
        }

        var originalLines = new Dictionary<int, string>();
        if (edit.OriginalContent is not null)
        {
            var splitLines = edit.OriginalContent.Split(new[] { Environment.NewLine }, StringSplitOptions.None);
            for (var index = 0; index < splitLines.Length; index++)
            {
                originalLines[index + 1] = splitLines[index];
            }
        }

        var newLines = new Dictionary<int, string>();
        if (edit.ModifiedContent is not null)
        {
            var splitLines = edit.ModifiedContent.Split(new[] { Environment.NewLine }, StringSplitOptions.None);
            for (var index = 0; index < splitLines.Length; index++)
            {
                newLines[index + 1] = splitLines[index];
            }
        }

        return new DescribeEditResult(
            success: true,
            error: null,
            edits:
            [
                new FileEdit(
                    path: edit.Path,
                    originalLines: originalLines.Count == 0 ? null : originalLines,
                    newLines: newLines.Count == 0 ? null : newLines,
                    delete: string.Equals(edit.Operation, "delete", StringComparison.Ordinal))
            ]);
    }
}

public sealed record ReadResult(bool success, string? error, string? content);

public sealed record SearchResult(bool success, List<SearchMatch> matches, int totalMatches, string? error = null);

public sealed record SearchMatch(string path, int? line, Dictionary<int, string>? lines);

public sealed record EditResult(bool success, string? error, string? editId);

public sealed record ApplyEditsResult(bool success, int? appliedCount = null, string? error = null);

public sealed record FilesystemOperationResult(bool success, string? error = null);

public sealed record DescribeEditResult(bool success, string? error, List<FileEdit>? edits);

public sealed record FileEdit(string path, Dictionary<int, string>? originalLines, Dictionary<int, string>? newLines, bool delete = false);

public sealed record ApplyEditsRequest(List<FileEdit> Edits);
