using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using Phantom.Workspaces.Llm.Interfaces;
using System.Text;

namespace Phantom.Workspaces.Llm;

public sealed class FilesystemServiceToolset : IToolset, IAsyncDisposable
{
    private readonly ILoggerFactory? loggerFactory;
    private readonly string? editStoreConnectionJson;
    private readonly SemaphoreSlim initializeLock = new(1, 1);
    private McpClient? client;

    public FilesystemServiceToolset(
        string? editStoreConnectionJson = null,
        ILoggerFactory? loggerFactory = null)
    {
        this.editStoreConnectionJson = editStoreConnectionJson;
        this.loggerFactory = loggerFactory;
    }

    public async Task<AITool[]> ListToolsAsync()
    {
        await this.initializeLock.WaitAsync();
        try
        {
            if (this.client is null)
            {
                this.client = await CreateClientAsync(this.loggerFactory);
            }

            var mcpTools = await this.client.ListToolsAsync(options: null, cancellationToken: CancellationToken.None);
            return mcpTools.Cast<AITool>().ToArray();
        }
        finally
        {
            this.initializeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (this.client is not null)
        {
            await this.client.DisposeAsync();
            this.client = null;
        }

        this.initializeLock.Dispose();
    }

    private async Task<McpClient> CreateClientAsync(ILoggerFactory? loggerFactory)
    {
        var transport = CreateTransport(this.editStoreConnectionJson);
        return await McpClient.CreateAsync(
            transport,
            null,
            loggerFactory,
            CancellationToken.None);
    }

    private static StdioClientTransport CreateTransport(string? editStoreConnectionJson)
    {
        var (command, arguments, workingDirectory) = ResolveCommand(editStoreConnectionJson);
        var transportOptions = new StdioClientTransportOptions
        {
            Name = "filesystem",
            Command = command,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
        };

        return new StdioClientTransport(transportOptions);
    }

    private static (string Command, IList<string> Arguments, string WorkingDirectory) ResolveCommand(
        string? editStoreConnectionJson)
    {
        var assemblyPath = typeof(FilesystemServiceToolset).Assembly.Location;
        if (string.IsNullOrWhiteSpace(assemblyPath))
        {
            throw new InvalidOperationException("Unable to resolve Phantom.Workspaces.Llm.Core assembly location.");
        }

        var workingDirectory = Directory.GetCurrentDirectory();
        var arguments = new List<string> { "filesystem-mcp-server-stdio" };
        if (!string.IsNullOrWhiteSpace(editStoreConnectionJson))
        {
            var connectionBytes = Encoding.UTF8.GetBytes(editStoreConnectionJson);
            arguments.Add("--filesystem-edit-store-connection-base64");
            arguments.Add(Convert.ToBase64String(connectionBytes));
        }

        var executablePath = Path.ChangeExtension(assemblyPath, ".exe");
        if (File.Exists(executablePath))
        {
            return (executablePath, arguments, workingDirectory);
        }

        if (!File.Exists(assemblyPath))
        {
            throw new InvalidOperationException($"Filesystem MCP host assembly not found: {assemblyPath}");
        }

        var dotnetArguments = new List<string> { assemblyPath };
        dotnetArguments.AddRange(arguments);
        return ("dotnet", dotnetArguments, workingDirectory);
    }
}
