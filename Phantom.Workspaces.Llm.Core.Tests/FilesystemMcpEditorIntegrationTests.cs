using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class FilesystemMcpEditorIntegrationTests
{
    [Fact]
    public async Task FilesystemMcpServer_EditDescribeApply_WorksOverStdio()
    {
        using var sandbox = new FileSandbox();
        var filePath = sandbox.WriteFile("editor.txt", "hello world");
        await using var client = await CreateFilesystemMcpClientAsync();

        var toolNames = (await client.ListToolsAsync())
            .Select(static tool => tool.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Contains("edit", toolNames);
        Assert.Contains("describe_edit", toolNames);
        Assert.Contains("edit_apply", toolNames);

        var editCallResult = await client.CallToolAsync(
            "edit",
            new Dictionary<string, object?>
            {
                ["edits"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["path"] = filePath,
                        ["searchText"] = "world",
                        ["replaceText"] = "there",
                    },
                },
                ["preview"] = true,
            },
            cancellationToken: CancellationToken.None);
        var editResult = ReadJsonResult(editCallResult);
        Assert.True(editResult.GetProperty("success").GetBoolean());
        var firstEdit = editResult.GetProperty("edits")[0];
        var editId = firstEdit.GetProperty("editId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(editId));
        Assert.Equal("hello world", File.ReadAllText(filePath));

        var describeCallResult = await client.CallToolAsync(
            "describe_edit",
            new Dictionary<string, object?> { ["editId"] = editId! },
            cancellationToken: CancellationToken.None);
        var describeResult = ReadJsonResult(describeCallResult);
        Assert.True(describeResult.GetProperty("success").GetBoolean());
        var describedEdits = describeResult.GetProperty("edits");
        var editsJson = JsonSerializer.Serialize(describedEdits);

        var applyCallResult = await client.CallToolAsync(
            "edit_apply",
            new Dictionary<string, object?> { ["editsJson"] = $$"""{"Edits":{{editsJson}}}""" },
            cancellationToken: CancellationToken.None);
        var applyResult = ReadJsonResult(applyCallResult);
        Assert.True(applyResult.GetProperty("success").GetBoolean());
        Assert.Equal(1, applyResult.GetProperty("appliedCount").GetInt32());
        Assert.Equal("hello there", File.ReadAllText(filePath));
    }

    private static async Task<McpClient> CreateFilesystemMcpClientAsync()
    {
        var assemblyPath = typeof(FilesystemMcpToolService).Assembly.Location;
        var executablePath = Path.ChangeExtension(assemblyPath, ".exe");
        var workingDirectory = Path.GetDirectoryName(assemblyPath)
            ?? Directory.GetCurrentDirectory();
        var options = new StdioClientTransportOptions
        {
            Name = "filesystem-test-mcp",
            WorkingDirectory = workingDirectory,
            Command = "dotnet",
        };

        if (File.Exists(executablePath))
        {
            options.Command = executablePath;
            options.Arguments = ["filesystem-mcp-server-stdio"];
        }
        else
        {
            options.Command = "dotnet";
            options.Arguments = [assemblyPath, "filesystem-mcp-server-stdio"];
        }

        var transport = new StdioClientTransport(options);
        return await McpClient.CreateAsync(transport);
    }

    private static JsonElement ReadJsonResult(CallToolResult result)
    {
        var content = Assert.Single(result.Content.OfType<TextContentBlock>());
        using var document = JsonDocument.Parse(content.Text);
        return document.RootElement.Clone();
    }

    private sealed class FileSandbox : IDisposable
    {
        public FileSandbox()
        {
            this.RootPath = Path.Combine(Path.GetTempPath(), "pw-fs-mcp-editor-tests-" + Guid.NewGuid().ToString("N"));
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
