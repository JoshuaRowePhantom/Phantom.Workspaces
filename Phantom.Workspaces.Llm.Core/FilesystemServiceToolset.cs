using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using System.Text;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// When changing filesystem toolset options, update the workspace
/// documentation entity: <c>["documentation", "agent-options", "tools"]</c>.
/// </summary>
public sealed class FilesystemServiceContextProvider : AIContextProvider, IAsyncDisposable
{
    private readonly string stateKey = $"filesystem-service:{Guid.NewGuid():n}";
    private readonly ILoggerFactory? loggerFactory;
    private readonly string? editStoreConnectionJson;
    private readonly SemaphoreSlim initializeLock = new(1, 1);
    private McpClient? client;

    public FilesystemServiceContextProvider(
        string? editStoreConnectionJson = null,
        ILoggerFactory? loggerFactory = null)
        : base(null, null, null)
    {
        this.editStoreConnectionJson = editStoreConnectionJson;
        this.loggerFactory = loggerFactory;
    }

    public override IReadOnlyList<string> StateKeys => [this.stateKey];

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        await this.initializeLock.WaitAsync(cancellationToken);
        try
        {
            if (this.client is null)
            {
                this.client = await CreateClientAsync(this.loggerFactory);
            }

            var mcpTools = await McpClientToolListing.ListToolsAsync(this.client, cancellationToken);
            return new AIContext
            {
                Tools = mcpTools.Cast<AITool>().ToArray(),
            };
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
        var assemblyPath = typeof(FilesystemServiceContextProvider).Assembly.Location;
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
