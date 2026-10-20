using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Services.Notifications;

namespace Phantom.Workspaces.Tools;

/// <summary>
/// A built-in scheduled tool that discovers any active VS Code dev tunnel for the current
/// user/machine through the output-free status resolver, then upserts a single
/// <c>vscode-tunnel</c> entity under the user-computer-profile. Results and logs contain
/// only safe outcomes. No entity is upserted when the CLI reports no running tunnel.
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
        INotificationService? notificationService = null,
        ILogger<VsCodeTunnelDiscoveryTool>? logger = null)
    {
        this.currentExecutionContextProvider = currentExecutionContextProvider ?? new CurrentExecutionContextProvider();
        this.logger = logger ?? NullLogger<VsCodeTunnelDiscoveryTool>.Instance;
        this.tunnelStatusResolver = tunnelStatusResolver
            ?? new VsCodeTunnelStatusResolver(
                invoker: new VsCodeCliInvoker(notificationService: notificationService, logger: this.logger),
                logger: this.logger);
        this.defaultCliPathResolver = defaultCliPathResolver ?? VsCodeCliLocator.ResolveDefaultCliPath;
    }

    public string ToolType => "vscode-tunnel-discovery";

    public async Task<WorkspaceToolExecutionResult> ExecuteAsync(WorkspaceToolExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var cliPath = this.ResolveCliPath(context.Tool.Data);

        VsCodeTunnelResolution resolution;
        try
        {
            resolution = await this.tunnelStatusResolver
                .ResolveAsync(cliPath, context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            return WorkspaceToolExecutionResult.Failure(
                "VS Code tunnel discovery: Cancelled.");
        }
        catch (Exception)
        {
            return WorkspaceToolExecutionResult.Failure("VS Code tunnel discovery: CommandFailed.");
        }

        if (resolution.Outcome is VsCodeTunnelOutcome.CommandFailed or VsCodeTunnelOutcome.InvalidStatus or VsCodeTunnelOutcome.Cancelled)
        {
            return WorkspaceToolExecutionResult.Failure(
                $"VS Code tunnel discovery: {resolution.Outcome}.");
        }

        if (resolution.Status is null)
        {
            // No tunnel currently running — do not upsert a stale entity. Report this as a
            // failure (#1355) so callers can distinguish "tunnel discovered" from "no tunnel
            // found"; a missing tunnel must not be treated as a successful discovery.
            var noTunnelSummary = "VS Code tunnel discovery: no tunnel running.";
            this.logger.LogInformation("{Summary}", noTunnelSummary);
            return WorkspaceToolExecutionResult.Failure(noTunnelSummary);
        }

        var entityName = this.BuildEntityName();
        var entityId = CreateDeterministicEntityId(entityName);

        using var entityDataDocument = JsonDocument.Parse(
            BuildVsCodeTunnelJson(entityId, entityName, resolution.Status.TunnelName, resolution.Status.TunnelUrl, resolution.Status.IsConnected));

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

        var summary = $"VS Code tunnel discovery: {resolution.Outcome}.";
        this.logger.LogInformation("{Summary}", summary);
        return WorkspaceToolExecutionResult.Success() with { ResultContent = summary };
    }

    private EntityName BuildEntityName()
    {
        var profileName = new EntityName(
            "computer-user-profiles",
            "users",
            "username",
            this.currentExecutionContextProvider.UserName,
            "computers",
            "hostname",
            this.currentExecutionContextProvider.EffectiveComputerName);
        return VsCodeTunnelEntityNaming.BuildTunnelName(profileName);
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
