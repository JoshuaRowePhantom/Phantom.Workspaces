using System.Text.Json;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class FilesystemMcpToolServiceTests
{
    [Fact]
    public void Read_ReturnsRequestedLineRange()
    {
        using var sandbox = new FileSandbox();
        var filePath = sandbox.WriteFile("a.txt", "one\ntwo\nthree\nfour");
        var service = CreateService();

        var result = service.Read(filePath, start: 2, end: 3);

        Assert.True(result.success);
        Assert.Equal("two" + Environment.NewLine + "three", result.content);
    }

    [Fact]
    public void Read_ReturnsError_WhenFileMissing()
    {
        using var sandbox = new FileSandbox();
        var service = CreateService();

        var result = service.Read(Path.Combine(sandbox.RootPath, "missing.txt"));

        Assert.False(result.success);
        Assert.Contains("File not found", result.error, StringComparison.Ordinal);
    }

    [Fact]
    public void Search_ListOnly_ReturnsMatchingPaths()
    {
        using var sandbox = new FileSandbox();
        var first = sandbox.WriteFile("alpha\\one.txt", "a");
        sandbox.WriteFile("alpha\\nested\\three.txt", "c");
        var service = CreateService();

        var result = service.Search(Path.Combine(sandbox.RootPath, "alpha"));

        Assert.True(result.success);
        Assert.Equal(1, result.totalMatches);
        Assert.Contains(result.matches, match => string.Equals(match.path, first, StringComparison.Ordinal));
        Assert.All(result.matches, match => Assert.Null(match.line));
    }

    [Fact]
    public void Search_ListOnly_RecursiveGlobWithDoubleStar_ReturnsNestedMatches()
    {
        using var sandbox = new FileSandbox();
        var first = sandbox.WriteFile("alpha\\one.txt", "a");
        var second = sandbox.WriteFile("alpha\\nested\\two.txt", "b");
        var service = CreateService();

        var result = service.Search(Path.Combine(sandbox.RootPath, "alpha", "**", "*.txt"), listOnly: true);

        Assert.True(result.success);
        Assert.Equal(2, result.totalMatches);
        Assert.Contains(result.matches, match => string.Equals(match.path, first, StringComparison.Ordinal));
        Assert.Contains(result.matches, match => string.Equals(match.path, second, StringComparison.Ordinal));
    }

    [Fact]
    public void Search_TextWithContext_ReturnsLineAndContext()
    {
        using var sandbox = new FileSandbox();
        var file = sandbox.WriteFile("context.txt", "line1\nmatch-line\nline3");
        var service = CreateService();

        var result = service.Search(file, text: "match", context: 1);

        Assert.True(result.success);
        var match = Assert.Single(result.matches);
        Assert.Equal(2, match.line);
        Assert.NotNull(match.lines);
        Assert.Equal("line1", match.lines![1]);
        Assert.Equal("match-line", match.lines[2]);
        Assert.Equal("line3", match.lines[3]);
    }

    [Fact]
    public async Task EditAsync_Preview_DoesNotMutateFile_AndStoresEdit()
    {
        using var sandbox = new FileSandbox();
        var file = sandbox.WriteFile("preview.txt", "hello world");
        var service = CreateService();

        var edit = await service.EditAsync(file, searchText: "world", replaceText: "there", preview: true);

        Assert.True(edit.success);
        Assert.False(string.IsNullOrWhiteSpace(edit.editId));
        Assert.Equal("hello world", File.ReadAllText(file));
    }

    [Fact]
    public async Task EditAsync_AppliesReplacement_WhenPreviewIsFalse()
    {
        using var sandbox = new FileSandbox();
        var file = sandbox.WriteFile("edit.txt", "hello world");
        var service = CreateService();

        var edit = await service.EditAsync(file, searchText: "world", replaceText: "there", preview: false);

        Assert.True(edit.success);
        Assert.Equal("hello there", File.ReadAllText(file));
    }

    [Fact]
    public async Task EditAsync_ReturnsError_WhenNoMatch()
    {
        using var sandbox = new FileSandbox();
        var file = sandbox.WriteFile("no-match.txt", "abc");
        var service = CreateService();

        var edit = await service.EditAsync(file, searchText: "zzz", replaceText: "x");

        Assert.False(edit.success);
        Assert.Contains("did not match", edit.error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DescribeEditAsync_ReturnsStoredLineSets()
    {
        using var sandbox = new FileSandbox();
        var file = sandbox.WriteFile("describe.txt", "a\nb");
        var service = CreateService();

        var edit = await service.EditAsync(file, searchText: "b", replaceText: "c", preview: true);
        var described = await service.DescribeEditAsync(edit.editId!);

        Assert.True(described.success);
        var describedEdit = Assert.Single(described.edits!);
        Assert.Equal("a", describedEdit.originalLines![1]);
        Assert.Equal("b", describedEdit.originalLines[2]);
        Assert.Equal("a", describedEdit.newLines![1]);
        Assert.Equal("c", describedEdit.newLines[2]);
    }

    [Fact]
    public async Task DescribeEditAsync_DeleteEdit_StoresFullBeforeAndAfterContent()
    {
        using var sandbox = new FileSandbox();
        var file = sandbox.WriteFile("delete-before-after.txt", "before");
        var service = CreateService();

        var edit = await service.EditAsync(file, preview: true, delete: true);
        var described = await service.DescribeEditAsync(edit.editId!);

        Assert.True(described.success);
        var describedEdit = Assert.Single(described.edits!);
        Assert.Equal("before", describedEdit.originalLines![1]);
        Assert.NotNull(describedEdit.newLines);
        Assert.Equal(string.Empty, describedEdit.newLines![1]);
    }

    [Fact]
    public void EditApply_AppliesProvidedLineChanges()
    {
        using var sandbox = new FileSandbox();
        var file = sandbox.WriteFile("apply.txt", "x\ny");
        var service = CreateService();

        var payload = JsonSerializer.Serialize(new ApplyEditsRequest(
        [
            new FileEdit(
                path: file,
                originalLines: null,
                newLines: new Dictionary<int, string> { [2] = "z" },
                delete: false)
        ]));

        var applied = service.EditApply(payload);

        Assert.True(applied.success);
        Assert.Equal(1, applied.appliedCount);
        Assert.Equal("x" + Environment.NewLine + "z", File.ReadAllText(file));
    }

    [Fact]
    public void EditApply_DeletesFile_WhenDeleteFlagSet()
    {
        using var sandbox = new FileSandbox();
        var file = sandbox.WriteFile("delete.txt", "content");
        var service = CreateService();

        var payload = JsonSerializer.Serialize(new ApplyEditsRequest(
        [
            new FileEdit(path: file, originalLines: null, newLines: null, delete: true)
        ]));

        var applied = service.EditApply(payload);

        Assert.True(applied.success);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void MakeDirectory_CreatesDirectory()
    {
        using var sandbox = new FileSandbox();
        var service = CreateService();
        var path = Path.Combine(sandbox.RootPath, "one", "two");

        var result = service.MakeDirectory(path);

        Assert.True(result.success);
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void RemoveItem_RemovesFile()
    {
        using var sandbox = new FileSandbox();
        var service = CreateService();
        var path = sandbox.WriteFile("remove-file.txt", "content");

        var result = service.RemoveItem(path);

        Assert.True(result.success);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void RemoveItem_DirectoryRequiresRecurse_WhenNonEmpty()
    {
        using var sandbox = new FileSandbox();
        var service = CreateService();
        var directory = Path.Combine(sandbox.RootPath, "non-empty");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "child.txt"), "x");

        var result = service.RemoveItem(directory, recurse: false);

        Assert.False(result.success);
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public void RemoveItem_RemovesDirectory_WhenRecurseTrue()
    {
        using var sandbox = new FileSandbox();
        var service = CreateService();
        var directory = Path.Combine(sandbox.RootPath, "remove-dir");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "child.txt"), "x");

        var result = service.RemoveItem(directory, recurse: true);

        Assert.True(result.success);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void MoveItem_MovesFile()
    {
        using var sandbox = new FileSandbox();
        var service = CreateService();
        var source = sandbox.WriteFile("move-source.txt", "content");
        var destination = Path.Combine(sandbox.RootPath, "nested", "move-destination.txt");

        var result = service.MoveItem(source, destination);

        Assert.True(result.success);
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(destination));
        Assert.Equal("content", File.ReadAllText(destination));
    }

    private static FilesystemMcpToolService CreateService()
        => new(new InMemoryFilesystemEditStore());

    private sealed class FileSandbox : IDisposable
    {
        public FileSandbox()
        {
            this.RootPath = Path.Combine(Path.GetTempPath(), "pw-fs-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(this.RootPath);
        }

        public string RootPath { get; }

        public string WriteFile(string relativePath, string content)
        {
            var fullPath = Path.Combine(this.RootPath, relativePath);
            var directory = Path.GetDirectoryName(fullPath) ?? this.RootPath;
            Directory.CreateDirectory(directory);
            File.WriteAllText(fullPath, content.Replace("\n", Environment.NewLine, StringComparison.Ordinal));
            return fullPath;
        }

        public void Dispose()
        {
            if (Directory.Exists(this.RootPath))
            {
                Directory.Delete(this.RootPath, recursive: true);
            }
        }
    }
}
