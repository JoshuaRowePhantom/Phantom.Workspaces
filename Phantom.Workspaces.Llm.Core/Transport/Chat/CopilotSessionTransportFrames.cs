using System.Text.Json;
using System.Text.Json.Nodes;
using GitHub.Copilot;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm.Core.Transport.Chat;

/// <summary>
/// Frame constants and JSON build/parse helpers for the per-component model executor binding
/// (issue #1443, per-component-executor-binding Commit 6B). This is the SDK-<b>session</b>-only
/// bridge: it transports ONLY the innermost <see cref="Phantom.Workspaces.Llm.Copilot.ICopilotSession"/>
/// (create / resume / send / event-pump / abort / set-model / dispose) while the router
/// (<c>IChatClient</c> decorators) and the <c>AIContextProviders</c> stay local. It is the deliberate
/// structural inverse of <c>ChatClientTransportListener</c>, which remotes the whole
/// <c>AgentChat</c>.
/// </summary>
/// <remarks>
/// <para>Only explicitly allowlisted session fields cross the boundary. <see cref="SessionConfig"/>
/// carries non-serialisable state, so the host rebuilds a fresh config from model / streaming /
/// working-directory / system-message / reasoning / tool-policy fields. Function declarations cross
/// without delegates; invocation is proxied back to the tool's owning caller. BYOK provider
/// configuration never crosses—only an opaque worker-local provider reference does.</para>
/// <para>Session <b>events</b> round-trip faithfully because the Copilot SDK exposes the public
/// polymorphic pair <see cref="SessionEvent.ToJson"/> / <see cref="SessionEvent.FromJson(string)"/>;
/// the host serialises each raised <see cref="SessionEvent"/> and the client rehydrates the concrete
/// subtype (including <see cref="AssistantMessageEvent"/>).</para>
/// </remarks>
internal static class CopilotSessionTransportFrames
{
    /// <summary>The connection-request <c>type</c> value that selects the client-only model host.</summary>
    public const string ConnectionType = "copilot-sdk-session";

    public const string TypeProperty = "type";

    // Client -> host request frames.
    public const string CreateSessionType = "create-session";
    public const string ResumeSessionType = "resume-session";
    public const string SendType = "send";
    public const string SendAndWaitType = "send-and-wait";
    public const string AbortType = "abort";
    public const string SetModelType = "set-model";
    public const string DisposeType = "dispose";

    // Host -> client response frames.
    public const string SessionCreatedType = "session-created";
    public const string SessionErrorType = "session-error";
    public const string SessionEventType = "session-event";
    public const string SendResultType = "send-result";
    public const string ToolInvokeType = "tool-invoke";
    public const string ToolResultType = "tool-result";
    public const string ToolErrorType = "tool-error";

    // Shared property names.
    public const string ConfigProperty = "config";
    public const string SessionIdProperty = "session-id";
    public const string ErrorProperty = "error";
    public const string EventJsonProperty = "event-json";
    public const string RequestIdProperty = "request-id";
    public const string ModelIdProperty = "model-id";
    public const string OptionsProperty = "options";
    public const string TrustProfileProperty = "trust-profile";
    public const string ExpectedTrustProfileRevisionProperty = "expected-trust-profile-revision";
    public const string CorrelationIdProperty = "correlation-id";
    public const string ProviderReferenceProperty = "provider-reference";
    public const string ErrorCategoryProperty = "error-category";
    public const string ToolCallIdProperty = "tool-call-id";
    public const string ToolNameProperty = "tool-name";
    public const string ArgumentsJsonProperty = "arguments-json";
    public const string ResultJsonProperty = "result-json";

    // Scalar session-config field names.
    public const string ConfigModel = "model";
    public const string ConfigStreaming = "streaming";
    public const string ConfigWorkingDirectory = "working-directory";
    public const string ConfigTools = "tools";
    public const string ConfigSystemMessage = "system-message";
    public const string ConfigReasoningEffort = "reasoning-effort";
    public const string ConfigAvailableTools = "available-tools";
    public const string ConfigExcludedTools = "excluded-tools";
    public const string ToolDescription = "description";
    public const string ToolJsonSchema = "json-schema";
    public const string ToolReturnJsonSchema = "return-json-schema";

    // Scalar message-options field names.
    public const string MessagePrompt = "prompt";
    public const string MessageMode = "mode";
    public const string MessageDisplayPrompt = "display-prompt";

    /// <summary>Builds the connection-request descriptor that selects the client-only model host.</summary>
    public static JsonElement BuildConnectionRequest(
        Phantom.Workspaces.Llm.Trust.AgentExecutionTrustProfileReference? trustProfileReference = null,
        string? providerReference = null,
        string? correlationId = null)
    {
        var obj = new JsonObject { [TypeProperty] = ConnectionType };
        if (trustProfileReference is not null)
        {
            obj[TrustProfileProperty] = trustProfileReference.Id;
            obj[ExpectedTrustProfileRevisionProperty] =
                trustProfileReference.ExpectedRevision;
        }
        if (providerReference is not null)
        {
            ValidateOpaqueReference(providerReference, nameof(providerReference));
            obj[ProviderReferenceProperty] = providerReference;
        }
        correlationId ??= Guid.NewGuid().ToString("N");
        if (!Guid.TryParseExact(correlationId, "N", out _))
        {
            throw new ArgumentException(
                "Correlation id must be a GUID in N format.",
                nameof(correlationId));
        }

        obj[CorrelationIdProperty] = correlationId;
        return JsonSerializer.SerializeToElement(obj);
    }

    /// <summary>True when <paramref name="request"/> is a <see cref="ConnectionType"/> connection request.</summary>
    public static bool IsConnectionRequest(JsonElement request)
        => request.ValueKind == JsonValueKind.Object
           && request.TryGetProperty(TypeProperty, out var type)
           && type.ValueKind == JsonValueKind.String
           && string.Equals(type.GetString(), ConnectionType, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the <c>type</c> discriminator of a frame, or <see langword="null"/>.</summary>
    public static string? FrameType(JsonElement frame)
        => frame.ValueKind == JsonValueKind.Object
           && frame.TryGetProperty(TypeProperty, out var type)
           && type.ValueKind == JsonValueKind.String
            ? type.GetString()
            : null;

    /// <summary>Projects a <see cref="SessionConfig"/> onto the forwarded scalar fields.</summary>
    public static JsonObject SerializeConfig(SessionConfigBase config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var obj = new JsonObject();
        if (!string.IsNullOrWhiteSpace(config.Model))
        {
            obj[ConfigModel] = config.Model;
        }

        obj[ConfigStreaming] = config.Streaming;
        if (!string.IsNullOrWhiteSpace(config.WorkingDirectory))
        {
            obj[ConfigWorkingDirectory] = config.WorkingDirectory;
        }
        if (!string.IsNullOrWhiteSpace(config.SystemMessage?.Content))
        {
            obj[ConfigSystemMessage] = config.SystemMessage.Content;
        }
        if (!string.IsNullOrWhiteSpace(config.ReasoningEffort))
        {
            obj[ConfigReasoningEffort] = config.ReasoningEffort;
        }
        if (config.AvailableTools is { Count: > 0 })
        {
            obj[ConfigAvailableTools] = new JsonArray(
                config.AvailableTools
                    .Select(static item => (JsonNode?)JsonValue.Create(item))
                    .ToArray());
        }
        if (config.ExcludedTools is { Count: > 0 })
        {
            obj[ConfigExcludedTools] = new JsonArray(
                config.ExcludedTools
                    .Select(static item => (JsonNode?)JsonValue.Create(item))
                    .ToArray());
        }
        if (config.Tools is { Count: > 0 })
        {
            var tools = new JsonArray();
            foreach (var tool in config.Tools)
            {
                tools.Add(new JsonObject
                {
                    [ToolNameProperty] = tool.Name,
                    [ToolDescription] = tool.Description,
                    [ToolJsonSchema] = JsonNode.Parse(tool.JsonSchema.GetRawText()),
                    [ToolReturnJsonSchema] = tool.ReturnJsonSchema is { } returnSchema
                        ? JsonNode.Parse(returnSchema.GetRawText())
                        : null,
                });
            }

            obj[ConfigTools] = tools;
        }

        return obj;
    }

    /// <summary>Rebuilds a fresh <see cref="SessionConfig"/> from the forwarded scalar fields.</summary>
    public static SessionConfig DeserializeSessionConfig(JsonElement configElement)
    {
        var config = new SessionConfig
        {
            OnPermissionRequest = PermissionHandler.ApproveAll,
        };
        ApplyBaseConfig(config, configElement);
        return config;
    }

    /// <summary>Rebuilds a fresh <see cref="ResumeSessionConfig"/> from the forwarded scalar fields.</summary>
    public static ResumeSessionConfig DeserializeResumeSessionConfig(JsonElement configElement)
    {
        var config = new ResumeSessionConfig
        {
            OnPermissionRequest = PermissionHandler.ApproveAll,
        };
        ApplyBaseConfig(config, configElement);
        return config;
    }

    /// <summary>Projects a <see cref="MessageOptions"/> onto the forwarded scalar fields.</summary>
    public static JsonObject SerializeMessageOptions(MessageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var obj = new JsonObject { [MessagePrompt] = options.Prompt ?? string.Empty };
        if (!string.IsNullOrWhiteSpace(options.Mode))
        {
            obj[MessageMode] = options.Mode;
        }

        if (!string.IsNullOrWhiteSpace(options.DisplayPrompt))
        {
            obj[MessageDisplayPrompt] = options.DisplayPrompt;
        }

        return obj;
    }

    /// <summary>Rebuilds a fresh <see cref="MessageOptions"/> from the forwarded scalar fields.</summary>
    public static MessageOptions DeserializeMessageOptions(JsonElement optionsElement)
    {
        var options = new MessageOptions
        {
            Prompt = GetString(optionsElement, MessagePrompt) ?? string.Empty,
        };

        if (GetString(optionsElement, MessageMode) is { } mode)
        {
            options.Mode = mode;
        }

        if (GetString(optionsElement, MessageDisplayPrompt) is { } displayPrompt)
        {
            options.DisplayPrompt = displayPrompt;
        }

        return options;
    }

    public static JsonElement BuildFrame(JsonObject frame) => JsonSerializer.SerializeToElement(frame);

    public static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static string GetRequiredCorrelationId(JsonElement request)
    {
        var correlationId = GetString(request, CorrelationIdProperty);
        if (!Guid.TryParseExact(correlationId, "N", out _))
        {
            throw new InvalidOperationException(
                "Remote Copilot connection requires a valid correlation id.");
        }

        return correlationId;
    }

    public static string? GetProviderReference(JsonElement request)
    {
        var providerReference = GetString(request, ProviderReferenceProperty);
        if (providerReference is not null)
        {
            ValidateOpaqueReference(providerReference, ProviderReferenceProperty);
        }

        return providerReference;
    }

    public static ICollection<AIFunctionDeclaration> DeserializeTools(
        JsonElement configElement,
        Func<string, string, JsonElement, JsonElement?, AIFunction> createTool)
    {
        ArgumentNullException.ThrowIfNull(createTool);
        if (configElement.ValueKind != JsonValueKind.Object
            || !configElement.TryGetProperty(ConfigTools, out var tools)
            || tools.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<AIFunctionDeclaration>();
        foreach (var tool in tools.EnumerateArray())
        {
            var name = GetString(tool, ToolNameProperty);
            var description = GetString(tool, ToolDescription) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name)
                || !tool.TryGetProperty(ToolJsonSchema, out var schema)
                || schema.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "Remote Copilot tool declaration is malformed.");
            }

            JsonElement? returnSchema = null;
            if (tool.TryGetProperty(ToolReturnJsonSchema, out var returned)
                && returned.ValueKind == JsonValueKind.Object)
            {
                returnSchema = returned.Clone();
            }

            result.Add(createTool(
                name,
                description,
                schema.Clone(),
                returnSchema));
        }

        return result;
    }

    public static bool TryGetTrustProfileReference(
        JsonElement request,
        out Phantom.Workspaces.Llm.Trust.AgentExecutionTrustProfileReference? reference)
    {
        reference = null;
        JsonElement profile = default;
        JsonElement revision = default;
        var hasProfile = request.ValueKind == JsonValueKind.Object
            && request.TryGetProperty(TrustProfileProperty, out profile);
        var hasRevision = request.ValueKind == JsonValueKind.Object
            && request.TryGetProperty(ExpectedTrustProfileRevisionProperty, out revision);
        if (!hasProfile && !hasRevision)
        {
            return false;
        }

        if (!hasProfile
            || !hasRevision
            || profile.ValueKind != JsonValueKind.String
            || revision.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(profile.GetString())
            || string.IsNullOrWhiteSpace(revision.GetString()))
        {
            throw new InvalidOperationException(
                "Remote Copilot trust intent requires non-empty string profile and revision fields.");
        }

        reference = new Phantom.Workspaces.Llm.Trust.AgentExecutionTrustProfileReference(
            "trust-profile",
            profile.GetString()!,
            revision.GetString()!);
        return true;
    }

    private static void ApplyBaseConfig(SessionConfigBase config, JsonElement configElement)
    {
        if (configElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (GetString(configElement, ConfigModel) is { } model)
        {
            config.Model = model;
        }

        if (configElement.TryGetProperty(ConfigStreaming, out var streaming)
            && (streaming.ValueKind == JsonValueKind.True || streaming.ValueKind == JsonValueKind.False))
        {
            config.Streaming = streaming.GetBoolean();
        }

        if (GetString(configElement, ConfigWorkingDirectory) is { } workingDirectory)
        {
            config.WorkingDirectory = workingDirectory;
        }
        if (GetString(configElement, ConfigSystemMessage) is { } systemMessage)
        {
            config.SystemMessage = new SystemMessageConfig { Content = systemMessage };
        }
        if (GetString(configElement, ConfigReasoningEffort) is { } reasoningEffort)
        {
            config.ReasoningEffort = reasoningEffort;
        }

        config.AvailableTools = ReadStringArray(
            configElement,
            ConfigAvailableTools);
        config.ExcludedTools = ReadStringArray(
            configElement,
            ConfigExcludedTools);
    }

    private static IList<string>? ReadStringArray(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var array))
        {
            return null;
        }
        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"Remote Copilot config field '{propertyName}' must be an array.");
        }

        var values = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw new InvalidOperationException(
                    $"Remote Copilot config field '{propertyName}' contains an invalid tool name.");
            }
            values.Add(item.GetString()!);
        }

        return values;
    }

    private static void ValidateOpaqueReference(string value, string parameterName)
    {
        if (!IsValidProviderReference(value))
        {
            throw new ArgumentException(
                "Provider reference must contain only letters, digits, '.', '-', or '_'.",
                parameterName);
        }
    }

    internal static bool IsValidProviderReference(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Length <= 128
           && value.All(static character =>
               char.IsAsciiLetterOrDigit(character)
               || character is '-' or '_' or '.');
}
