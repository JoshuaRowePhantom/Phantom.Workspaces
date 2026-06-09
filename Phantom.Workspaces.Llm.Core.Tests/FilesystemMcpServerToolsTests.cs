using Phantom.Workspaces.Host;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class FilesystemMcpServerToolsTests
{
    [Fact]
    public async Task Edit_ReturnsError_WhenNoRequestsProvided()
    {
        var tools = CreateTools();

        var result = await tools.Edit([], preview: false);

        Assert.False(result.success);
        Assert.Contains("At least one edit request", result.error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edit_PreviewMode_DoesNotApplyChanges()
    {
        using var sandbox = new FileSandbox();
        var filePath = sandbox.WriteFile("preview.txt", "hello world");
        var tools = CreateTools();

        var result = await tools.Edit(
        [
            new FilesystemEditRequest(
                path: filePath,
                searchText: "world",
                replaceText: "there")
        ],
        preview: true);

        Assert.True(result.success);
        Assert.Single(result.edits);
        Assert.Equal("hello world", File.ReadAllText(filePath));
    }

    [Fact]
    public async Task Edit_AppliesSequentialEdits_ForSameAndDifferentFiles()
    {
        using var sandbox = new FileSandbox();
        var firstPath = sandbox.WriteFile("first.txt", "alpha beta");
        var secondPath = sandbox.WriteFile("second.txt", "one two");
        var tools = CreateTools();

        var result = await tools.Edit(
        [
            new FilesystemEditRequest(path: firstPath, searchText: "alpha", replaceText: "ALPHA"),
            new FilesystemEditRequest(path: firstPath, searchText: "beta", replaceText: "BETA"),
            new FilesystemEditRequest(path: secondPath, searchText: "two", replaceText: "TWO")
        ],
        preview: false);

        Assert.True(result.success);
        Assert.Equal(3, result.edits.Count);
        Assert.Equal("ALPHA BETA", File.ReadAllText(firstPath));
        Assert.Equal("one TWO", File.ReadAllText(secondPath));
    }

    private static FilesystemMcpServerTools CreateTools()
        => new(new FilesystemMcpToolService(new InMemoryFilesystemEditStore()));

    private sealed class FileSandbox : IDisposable
    {
        public FileSandbox()
        {
            this.RootPath = Path.Combine(Path.GetTempPath(), "pw-fs-host-tests-" + Guid.NewGuid().ToString("N"));
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
