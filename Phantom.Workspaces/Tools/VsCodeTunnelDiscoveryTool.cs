using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Tools;

/// <summary>
/// A built-in scheduled tool that discovers any active VS Code dev tunnel for the current
/// user/machine and upserts a single <c>vscode-tunnel</c> entity under the user-computer-profile,
/// named at the fixed leaf <c>vscode-tunnel</c>.
/// </summary>
public sealed class VsCodeTunnelDiscoveryTool : IWorkspaceTool
{
    /// <summary>Optional tool-entity property overriding the VS Code CLI executable path.</summary>
    public const string CliPathProperty = "cli-path";

    /// <summary>Optional tool-entity property overriding the VS Code tunnel JSON file path.</summary>
    public const string TunnelJsonPathProperty = "tunnel-json-path";

    private readonly ICurrentExecutionContextProvider currentExecutionContextProvider;
    private readonly Func<string, CancellationToken, Task<int>>? processRunner;
    private readonly Func<string> defaultCliPathResolver;

    public VsCodeTunnelDiscoveryTool(
        ICurrentExecutionContextProvider? currentExecutionContextProvider = null,
        Func<string, CancellationToken, Task<int>>? processRunner = null,
        Func<string>? defaultCliPathResolver = null)
    {
        this.currentExecutionContextProvider = currentExecutionContextProvider ?? new CurrentExecutionContextProvider();
        this.processRunner = processRunner;
        this.defaultCliPathResolver = defaultCliPathResolver ?? VsCodeCliLocator.ResolveDefaultCliPath;
    }

    public string ToolType => "vscode-tunnel-discovery";

    public async Task<WorkspaceToolExecutionResult> ExecuteAsync(WorkspaceToolExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var tunnelJsonPath = ResolveTunnelJsonPath(context.Tool.Data);
        var tunnelName = ReadTunnelNameFromFile(tunnelJsonPath);

        if (tunnelName is null)
        {
            return WorkspaceToolExecutionResult.Failure($"Tunnel JSON not found or unreadable at {tunnelJsonPath}");
        }

        var cliPath = this.ResolveCliPath(context.Tool.Data);
        bool active;
        try
        {
            active = await this.CheckTunnelActiveAsync(cliPath, context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return WorkspaceToolExecutionResult.Failure($"Failed to run VS Code CLI: {ex.Message}");
        }

        var entityName = this.BuildEntityName();
        var entityId = CreateDeterministicEntityId(entityName);
        var tunnelUrl = $"https://vscode.dev/tunnel/{tunnelName}";

        using var entityDataDocument = JsonDocument.Parse(
            BuildVsCodeTunnelJson(entityId, entityName, tunnelName, tunnelUrl, active));

        await context.DataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "Discover VS Code dev tunnel." } },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = entityId,
                        ConcurrencyTag = null,
                        Data = entityDataDocument.RootElement.Clone(),
                        EntityChangeMode = EntityChangeMode.Replace,
                    }
                ],
            },
            context.CancellationToken).ConfigureAwait(false);

        return WorkspaceToolExecutionResult.Success();
    }

    private EntityName BuildEntityName()
    {
        return new EntityName(
            "computer-user-profiles",
            "users",
            "username",
            this.currentExecutionContextProvider.UserName,
            "computers",
            "hostname",
            this.currentExecutionContextProvider.EffectiveComputerName,
            "vscode-tunnel");
    }

    /// <summary>
    /// The default VS Code tunnel JSON path: <c>&lt;user home&gt;/.vscode/cli/code_tunnel.json</c>.
    /// The user home is read from the <c>USERPROFILE</c> environment variable when set (so discovery
    /// can be tested against an isolated home), falling back to the OS user-profile folder.
    /// </summary>
    public static string GetDefaultTunnelJsonPath()
    {
        var home = Environment.GetEnvironmentVariable("USERPROFILE");
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return Path.Combine(home, ".vscode", "cli", "code_tunnel.json");
    }

    private static string ResolveTunnelJsonPath(JsonElement? toolData)
    {
        if (toolData is JsonElement toolDataValue
            && toolDataValue.ValueKind == JsonValueKind.Object
            && toolDataValue.TryGetProperty(TunnelJsonPathProperty, out var pathElement)
            && pathElement.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(pathElement.GetString()))
        {
            return pathElement.GetString()!;
        }

        return GetDefaultTunnelJsonPath();
    }

    private static string? ReadTunnelNameFromFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("tunnel_name", out var tunnelNameElement)
                && tunnelNameElement.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(tunnelNameElement.GetString()))
            {
                return tunnelNameElement.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private string ResolveCliPath(JsonElement? toolData)
    {
        if (toolData is JsonElement toolDataValue
            && toolDataValue.ValueKind == JsonValueKind.Object
            && toolDataValue.TryGetProperty(CliPathProperty, out var pathElement)
            && pathElement.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(pathElement.GetString()))
        {
            return pathElement.GetString()!;
        }

        return this.defaultCliPathResolver();
    }

    private async Task<bool> CheckTunnelActiveAsync(string cliPath, CancellationToken cancellationToken)
    {
        var runner = this.processRunner ?? DefaultRunTunnelStatusAsync;
        var exitCode = await runner(cliPath, cancellationToken).ConfigureAwait(false);
        return exitCode == 0;
    }

    private static async Task<int> DefaultRunTunnelStatusAsync(string cliPath, CancellationToken cancellationToken)
    {
        var psi = VsCodeCliLocator.BuildProcessStartInfo(cliPath, "tunnel status");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process: {cliPath}");

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }

    private static EntityId CreateDeterministicEntityId(EntityName entityName)
    {
        var canonicalName = JsonSerializer.Serialize(entityName.Components);
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(canonicalName));
        return new EntityId(new Guid(hash));
    }

    private static string BuildVsCodeTunnelJson(
        EntityId entityId,
        EntityName entityName,
        string tunnelName,
        string tunnelUrl,
        bool active)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("entity-id", entityId.Value.ToString());

            writer.WritePropertyName("entity-types");
            writer.WriteStartArray();
            writer.WriteStringValue("entity");
            writer.WriteStringValue("vscode-tunnel");
            writer.WriteEndArray();

            writer.WritePropertyName("names");
            writer.WriteStartArray();
            writer.WriteStartArray();
            foreach (var component in entityName.Components)
            {
                writer.WriteStringValue(component);
            }

            writer.WriteEndArray();
            writer.WriteEndArray();

            writer.WritePropertyName("display-name");
            writer.WriteStartObject();
            writer.WriteString("default", "VS Code Dev Tunnel");
            writer.WriteEndObject();

            writer.WriteString("tunnel-name", tunnelName);
            writer.WriteString("tunnel-url", tunnelUrl);
            writer.WriteBoolean("active", active);

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
