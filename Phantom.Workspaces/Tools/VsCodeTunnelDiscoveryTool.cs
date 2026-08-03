using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Tools;

/// <summary>
/// A built-in scheduled tool that discovers any active VS Code dev tunnel for the current
/// user/machine by invoking <c>code tunnel status</c> and parsing its stdout, then upserts a
/// single <c>vscode-tunnel</c> entity under the user-computer-profile, named at the fixed leaf
/// <c>vscode-tunnel</c>. When the CLI reports that no tunnel is running the tool does not upsert
/// any entity.
/// </summary>
public sealed class VsCodeTunnelDiscoveryTool : IWorkspaceTool
{
    /// <summary>Optional tool-entity property overriding the VS Code CLI executable path.</summary>
    public const string CliPathProperty = "cli-path";

    private readonly ICurrentExecutionContextProvider currentExecutionContextProvider;
    private readonly ILogger<VsCodeTunnelDiscoveryTool> logger;
    private readonly IVsCodeTunnelStatusResolver tunnelStatusResolver;
    private readonly Func<string> defaultCliPathResolver;

    public VsCodeTunnelDiscoveryTool(
        ICurrentExecutionContextProvider? currentExecutionContextProvider = null,
        IVsCodeTunnelStatusResolver? tunnelStatusResolver = null,
        Func<string>? defaultCliPathResolver = null,
        ILogger<VsCodeTunnelDiscoveryTool>? logger = null)
    {
        this.currentExecutionContextProvider = currentExecutionContextProvider ?? new CurrentExecutionContextProvider();
        this.logger = logger ?? NullLogger<VsCodeTunnelDiscoveryTool>.Instance;
        this.tunnelStatusResolver = tunnelStatusResolver ?? new VsCodeTunnelStatusResolver(this.logger);
        this.defaultCliPathResolver = defaultCliPathResolver ?? VsCodeCliLocator.ResolveDefaultCliPath;
    }

    public string ToolType => "vscode-tunnel-discovery";

    public async Task<WorkspaceToolExecutionResult> ExecuteAsync(WorkspaceToolExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var cliPath = this.ResolveCliPath(context.Tool.Data);

        VsCodeTunnelStatus? status;
        try
        {
            status = await this.tunnelStatusResolver
                .GetTunnelStatusAsync(cliPath, context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (Win32Exception ex)
        {
            return WorkspaceToolExecutionResult.Failure(
                $"VS Code CLI ('code') not found or not runnable: {ex.Message}");
        }
        catch (Exception ex)
        {
            return WorkspaceToolExecutionResult.Failure($"Failed to run 'code tunnel status': {ex.Message}");
        }

        if (status is null)
        {
            // No tunnel currently running — do not upsert a stale entity.
            return WorkspaceToolExecutionResult.Success();
        }

        var entityName = this.BuildEntityName();
        var entityId = CreateDeterministicEntityId(entityName);

        using var entityDataDocument = JsonDocument.Parse(
            BuildVsCodeTunnelJson(entityId, entityName, status.TunnelName, status.TunnelUrl, status.IsConnected));

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

    private static EntityId CreateDeterministicEntityId(EntityName entityName)
    {
        return DeterministicEntityId.Create(entityName.Components);
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
