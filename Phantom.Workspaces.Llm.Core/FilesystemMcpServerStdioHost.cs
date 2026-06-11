using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace Phantom.Workspaces.Llm;

internal static class FilesystemMcpServerStdioHost
{
    public static async Task RunAsync(
        CancellationToken cancellationToken,
        string? editStoreConnectionJson = null)
    {
        var editStore = await FilesystemEditStoreFactory.CreateAsync(editStoreConnectionJson, cancellationToken);
        var builder = global::Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.AddConsole(consoleLogOptions =>
        {
            consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
        });

        builder.Services.AddSingleton<IFilesystemEditStore>(editStore);
        builder.Services.AddSingleton<FilesystemMcpToolService>();
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        await builder.Build().RunAsync(cancellationToken);
    }
}

[McpServerToolType]
public sealed class FilesystemMcpServerTools
{
    private readonly FilesystemMcpToolService service;

    public FilesystemMcpServerTools(FilesystemMcpToolService service)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
    }

    [McpServerTool, Description("Read file content with optional line range (start/end are 1-indexed).")]
    public ReadResult Read(string path, int? start = null, int? end = null)
        => this.service.Read(path, start, end);

    [McpServerTool, Description("Search a file, directory, or glob. A plain directory path lists files in that directory only; use ** in the glob to search recursively.")]
    public SearchResult Search(
        string path,
        string? pattern = null,
        string? text = null,
        bool listOnly = false,
        int? beforeContext = null,
        int? afterContext = null,
        int? context = null)
        => this.service.Search(path, pattern, text, listOnly, beforeContext, afterContext, context);

    [McpServerTool, Description("Create a directory, including parent directories when needed.")]
    public FilesystemOperationResult make_directory(string path)
        => this.service.MakeDirectory(path);

    [McpServerTool, Description("Remove a file or directory. Directories require recurse=true when non-empty.")]
    public FilesystemOperationResult remove_item(string path, bool recurse = false)
        => this.service.RemoveItem(path, recurse);

    [McpServerTool, Description("Move or rename a file or directory.")]
    public FilesystemOperationResult move_item(string sourcePath, string destinationPath)
        => this.service.MoveItem(sourcePath, destinationPath);

    [McpServerTool, Description("Create one or more previewable filesystem edits and store edit-ids.")]
    public async Task<EditBatchResult> Edit(
        List<FilesystemEditRequest> edits,
        bool preview = false)
    {
        if (edits is null || edits.Count == 0)
        {
            return new EditBatchResult(success: false, edits: [], error: "At least one edit request is required.");
        }

        var results = new List<EditResult>(edits.Count);
        foreach (var request in edits)
        {
            var previewResult = await this.service.EditAsync(
                request.path,
                request.searchText,
                request.searchRegex,
                request.replaceText,
                request.replaceRegex,
                preview: true,
                delete: request.delete);
            results.Add(previewResult);
            if (!previewResult.success || string.IsNullOrWhiteSpace(previewResult.editId))
            {
                return new EditBatchResult(success: false, edits: results, error: previewResult.error ?? "Failed to generate edit preview.");
            }

            if (preview)
            {
                continue;
            }

            var described = await this.service.DescribeEditAsync(previewResult.editId);
            if (!described.success || described.edits is null)
            {
                return new EditBatchResult(
                    success: false,
                    edits: results,
                    error: described.error ?? "Failed to describe generated edit.");
            }

            var applyPayload = JsonSerializer.Serialize(new ApplyEditsRequest(described.edits));
            var applyResult = this.service.EditApply(applyPayload);
            if (!applyResult.success)
            {
                return new EditBatchResult(
                    success: false,
                    edits: results,
                    error: applyResult.error ?? "Failed to apply generated edit.");
            }
        }

        return new EditBatchResult(success: true, edits: results, error: null);
    }

    [McpServerTool, Description("Apply explicit edits payload created from describe-edit output.")]
    public ApplyEditsResult EditApply(string editsJson)
        => this.service.EditApply(editsJson);

    [McpServerTool, Description("Resolve an edit-id to editable line deltas.")]
    public Task<DescribeEditResult> DescribeEdit(string editId)
        => this.service.DescribeEditAsync(editId);
}

public sealed record FilesystemEditRequest(
    string path,
    string? searchText = null,
    string? searchRegex = null,
    string? replaceText = null,
    string? replaceRegex = null,
    bool delete = false);

public sealed record EditBatchResult(bool success, List<EditResult> edits, string? error = null);
