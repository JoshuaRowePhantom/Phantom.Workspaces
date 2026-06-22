using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Tools;

/// <summary>
/// A built-in scheduled tool that discovers local GitHub Copilot CLI sessions on the host and
/// represents each as an <c>agent-definition</c> entity, so the sessions surface as agents in the
/// workspace. Each session directory under the Copilot session-state root is named by its session
/// id (a GUID); the agent-definition entity uses that GUID as its entity id, so re-running the tool
/// updates rather than duplicates. Sessions are named under the <c>copilot/sessions</c> namespace.
///
/// The tool also discovers installed MCP servers from the Copilot MCP configuration and registers
/// each as an <c>mcp-server</c> entity under the current machine's
/// <c>computer-user-profiles/.../copilot/mcp-servers</c> area, so machine-specific MCP servers can
/// be referenced by name from agent manifest tool resources.
/// </summary>
public sealed class CopilotSessionDiscoveryTool : IWorkspaceTool
{
    /// <summary>The optional tool-entity property overriding the Copilot session-state root directory.</summary>
    public const string SessionStateRootProperty = "session-state-root";

    /// <summary>The optional tool-entity property overriding the Copilot MCP configuration file path.</summary>
    public const string McpConfigPathProperty = "mcp-config-path";

    private readonly ICurrentExecutionContextProvider currentExecutionContextProvider;

    public CopilotSessionDiscoveryTool(
        ICurrentExecutionContextProvider? currentExecutionContextProvider = null)
    {
        this.currentExecutionContextProvider = currentExecutionContextProvider ?? new CurrentExecutionContextProvider();
    }

    public string ToolType => "copilot-session-discovery";

    public async Task<WorkspaceToolExecutionResult> ExecuteAsync(WorkspaceToolExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var changes = new List<EntityChange>();
        this.CollectSessionChanges(context, changes);
        this.CollectMcpServerChanges(context, changes);

        if (changes.Count == 0)
        {
            return new WorkspaceToolExecutionResult();
        }

        await context.DataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "Discover local GitHub Copilot CLI sessions and installed MCP servers." } },
                Changes = changes,
            },
            context.CancellationToken).ConfigureAwait(false);

        return new WorkspaceToolExecutionResult();
    }

    private void CollectSessionChanges(WorkspaceToolExecutionContext context, List<EntityChange> changes)
    {
        var root = ResolveSessionStateRoot(context.Tool.Data);
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var sessionDirectory in Directory.EnumerateDirectories(root))
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var sessionId = Path.GetFileName(sessionDirectory);
            if (!Guid.TryParse(sessionId, out var sessionGuid))
            {
                continue;
            }

            using var document = JsonDocument.Parse(BuildAgentDefinitionJson(sessionGuid, sessionId));
            changes.Add(new EntityChange
            {
                EntityId = new EntityId(sessionGuid),
                ConcurrencyTag = null,
                Data = document.RootElement.Clone(),
                EntityChangeMode = EntityChangeMode.Replace,
            });
        }
    }

    private void CollectMcpServerChanges(WorkspaceToolExecutionContext context, List<EntityChange> changes)
    {
        var mcpConfigPath = ResolveMcpConfigPath(context.Tool.Data);
        if (!File.Exists(mcpConfigPath))
        {
            return;
        }

        JsonDocument configDocument;
        try
        {
            configDocument = JsonDocument.Parse(File.ReadAllText(mcpConfigPath));
        }
        catch (JsonException)
        {
            return;
        }

        using (configDocument)
        {
            if (!configDocument.RootElement.TryGetProperty("mcpServers", out var servers)
                || servers.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var mcpServersPrefix = this.GetMachineMcpServersPrefix();
            foreach (var server in servers.EnumerateObject())
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                if (server.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var entityName = new EntityName([.. mcpServersPrefix.Components, server.Name]);
                using var document = JsonDocument.Parse(BuildMcpServerJson(entityName, server.Name, server.Value));
                changes.Add(new EntityChange
                {
                    EntityId = CreateDeterministicEntityId(entityName),
                    ConcurrencyTag = null,
                    Data = document.RootElement.Clone(),
                    EntityChangeMode = EntityChangeMode.Replace,
                });
            }
        }
    }

    /// <summary>
    /// The default Copilot session-state root: <c>&lt;user home&gt;/.copilot/session-state</c>. The
    /// user home is read from the <c>USERPROFILE</c> environment variable when set (so discovery can
    /// be tested against an isolated home), falling back to the OS user-profile folder.
    /// </summary>
    public static string GetDefaultSessionStateRoot()
    {
        var home = Environment.GetEnvironmentVariable("USERPROFILE");
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return Path.Combine(home, ".copilot", "session-state");
    }

    private static string ResolveSessionStateRoot(JsonElement? toolEntity)
    {
        if (toolEntity is JsonElement toolEntityValue
            && toolEntityValue.ValueKind == JsonValueKind.Object
            && toolEntityValue.TryGetProperty(SessionStateRootProperty, out var rootElement)
            && rootElement.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(rootElement.GetString()))
        {
            return rootElement.GetString()!;
        }

        return GetDefaultSessionStateRoot();
    }

    /// <summary>
    /// The default Copilot MCP configuration path: <c>&lt;user home&gt;/.copilot/mcp-config.json</c>.
    /// </summary>
    public static string GetDefaultMcpConfigPath()
    {
        var home = Environment.GetEnvironmentVariable("USERPROFILE");
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return Path.Combine(home, ".copilot", "mcp-config.json");
    }

    private static string ResolveMcpConfigPath(JsonElement? toolEntity)
    {
        if (toolEntity is JsonElement toolEntityValue
            && toolEntityValue.ValueKind == JsonValueKind.Object
            && toolEntityValue.TryGetProperty(McpConfigPathProperty, out var pathElement)
            && pathElement.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(pathElement.GetString()))
        {
            return pathElement.GetString()!;
        }

        return GetDefaultMcpConfigPath();
    }

    private EntityName GetMachineMcpServersPrefix()
    {
        return new EntityName(
            "computer-user-profiles",
            "users",
            "username",
            this.currentExecutionContextProvider.UserName,
            "computers",
            "hostname",
            this.currentExecutionContextProvider.ComputerName,
            "copilot",
            "mcp-servers");
    }

    private static EntityId CreateDeterministicEntityId(EntityName entityName)
    {
        var canonicalName = JsonSerializer.Serialize(entityName.Components);
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(canonicalName));
        return new EntityId(new Guid(hash));
    }

    private static string BuildMcpServerJson(EntityName entityName, string serverName, JsonElement serverConfiguration)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("entity-id", CreateDeterministicEntityId(entityName).Value.ToString());

            writer.WritePropertyName("entity-types");
            writer.WriteStartArray();
            writer.WriteStringValue("mcp-server");
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
            writer.WriteString("default", $"{serverName} MCP Server");
            writer.WriteEndObject();

            writer.WritePropertyName("mcp-server");
            WriteMcpServerConfiguration(writer, serverName, serverConfiguration);

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteMcpServerConfiguration(Utf8JsonWriter writer, string serverName, JsonElement serverConfiguration)
    {
        writer.WriteStartObject();
        writer.WriteString("serverName", serverName);

        writer.WritePropertyName("connection");
        WriteMcpConnection(writer, serverConfiguration);

        writer.WritePropertyName("approvalMode");
        writer.WriteStartObject();
        writer.WriteString("kind", "never");
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    private static void WriteMcpConnection(Utf8JsonWriter writer, JsonElement serverConfiguration)
    {
        // Remote servers declare a "url"; local stdio servers declare a "command" plus optional "args".
        if (serverConfiguration.TryGetProperty("url", out var url)
            && url.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(url.GetString()))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "Anonymous");
            writer.WriteString("endpoint", url.GetString());
            writer.WriteEndObject();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("kind", "Anonymous");
        writer.WriteString("endpoint", BuildStdioEndpoint(serverConfiguration));
        writer.WriteEndObject();
    }

    private static string BuildStdioEndpoint(JsonElement serverConfiguration)
    {
        var command = serverConfiguration.TryGetProperty("command", out var commandElement)
            && commandElement.ValueKind == JsonValueKind.String
            ? commandElement.GetString()
            : null;

        var builder = new StringBuilder("stdio://");
        builder.Append("?command=");
        builder.Append(Uri.EscapeDataString(command ?? string.Empty));

        if (serverConfiguration.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
        {
            foreach (var arg in args.EnumerateArray())
            {
                if (arg.ValueKind == JsonValueKind.String && arg.GetString() is { } argument)
                {
                    builder.Append("&arg=");
                    builder.Append(Uri.EscapeDataString(argument));
                }
            }
        }

        if (serverConfiguration.TryGetProperty("cwd", out var cwd)
            && cwd.ValueKind == JsonValueKind.String
            && cwd.GetString() is { Length: > 0 } workingDirectory)
        {
            builder.Append("&cwd=");
            builder.Append(Uri.EscapeDataString(workingDirectory));
        }

        return builder.ToString();
    }

    private static string BuildAgentDefinitionJson(Guid sessionGuid, string sessionId)
    {
        var shortId = sessionId.Length >= 8 ? sessionId[..8] : sessionId;
        var displayName = $"Copilot session {shortId}";

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("entity-id", sessionGuid.ToString());

            writer.WritePropertyName("entity-types");
            writer.WriteStartArray();
            writer.WriteStringValue("agent-definition");
            writer.WriteEndArray();

            writer.WritePropertyName("names");
            writer.WriteStartArray();
            writer.WriteStartArray();
            writer.WriteStringValue("copilot");
            writer.WriteStringValue("sessions");
            writer.WriteStringValue(sessionId);
            writer.WriteEndArray();
            writer.WriteEndArray();

            writer.WritePropertyName("display-name");
            writer.WriteStartObject();
            writer.WriteString("default", displayName);
            writer.WriteEndObject();

            writer.WritePropertyName("definition");
            WriteDefinition(writer, sessionId, displayName);

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteDefinition(Utf8JsonWriter writer, string sessionId, string displayName)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", "prompt");
        writer.WriteString("name", $"copilot-session-{sessionId}");
        writer.WriteString("displayName", displayName);
        writer.WriteString("description", "A discovered local GitHub Copilot CLI session, surfaced as an agent.");

        writer.WritePropertyName("model");
        writer.WriteStartObject();
        writer.WriteString("id", "auto");
        writer.WriteString("provider", "github-copilot");
        writer.WriteString("apiType", "OpenAI");
        writer.WritePropertyName("connection");
        writer.WriteStartObject();
        writer.WriteString("kind", "key");
        writer.WriteString("apiKey", "${GITHUB_TOKEN}");
        writer.WriteEndObject();
        writer.WriteEndObject();

        writer.WriteString("instructions", "You are a helpful AI assistant powered by the GitHub Copilot SDK.");

        writer.WritePropertyName("tools");
        writer.WriteStartArray();
        writer.WriteStartObject();
        writer.WriteString("kind", "workspace-entity");
        writer.WriteString("description", "Read and modify workspace entities.");
        writer.WriteEndObject();
        writer.WriteEndArray();

        writer.WritePropertyName("metadata");
        writer.WriteStartObject();
        writer.WriteString("version", "1.0");
        writer.WriteString("author", "Phantom Workspaces");
        writer.WriteString("copilot-session-id", sessionId);
        writer.WritePropertyName("tags");
        writer.WriteStartArray();
        writer.WriteStringValue("github-copilot");
        writer.WriteStringValue("copilot-session");
        writer.WriteEndArray();
        writer.WriteEndObject();

        writer.WriteEndObject();
    }
}
