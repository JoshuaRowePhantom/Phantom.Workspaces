using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
/// updates rather than duplicates.
/// </summary>
public sealed class CopilotSessionDiscoveryTool : IWorkspaceTool
{
    /// <summary>The optional tool-entity property overriding the Copilot session-state root directory.</summary>
    public const string SessionStateRootProperty = "session-state-root";

    public string ToolType => "copilot-session-discovery";

    public async Task<WorkspaceToolExecutionResult> ExecuteAsync(WorkspaceToolExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var root = ResolveSessionStateRoot(context.Tool.Data);
        if (!Directory.Exists(root))
        {
            return new WorkspaceToolExecutionResult();
        }

        var changes = new List<EntityChange>();
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

        if (changes.Count == 0)
        {
            return new WorkspaceToolExecutionResult();
        }

        await context.DataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "Discover local GitHub Copilot CLI sessions." } },
                Changes = changes,
            },
            context.CancellationToken).ConfigureAwait(false);

        return new WorkspaceToolExecutionResult();
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
            writer.WriteStringValue("copilot-sessions");
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
