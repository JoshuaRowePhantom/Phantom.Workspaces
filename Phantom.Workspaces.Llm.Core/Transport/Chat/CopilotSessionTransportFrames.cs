using System.Text.Json;
using System.Text.Json.Nodes;
using GitHub.Copilot;

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
/// <para>Only wire-serialisable scalars cross the boundary. <see cref="SessionConfig"/> carries
/// non-serialisable state (delegates such as <c>OnPermissionRequest</c> and
/// <see cref="Microsoft.Extensions.AI.AIFunction"/> tools), so the host rebuilds a fresh
/// <see cref="SessionConfig"/> from the forwarded scalar fields (model / streaming / working
/// directory / system message) rather than serialising the source config. Local tool execution
/// and full manifest wiring are completed by the flagship split-executor commit (#1441).</para>
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

    // Shared property names.
    public const string ConfigProperty = "config";
    public const string SessionIdProperty = "session-id";
    public const string ErrorProperty = "error";
    public const string EventJsonProperty = "event-json";
    public const string RequestIdProperty = "request-id";
    public const string ModelIdProperty = "model-id";
    public const string OptionsProperty = "options";

    // Scalar session-config field names.
    public const string ConfigModel = "model";
    public const string ConfigStreaming = "streaming";
    public const string ConfigWorkingDirectory = "working-directory";

    // Scalar message-options field names.
    public const string MessagePrompt = "prompt";
    public const string MessageMode = "mode";
    public const string MessageDisplayPrompt = "display-prompt";

    /// <summary>Builds the connection-request descriptor that selects the client-only model host.</summary>
    public static JsonElement BuildConnectionRequest()
    {
        var obj = new JsonObject { [TypeProperty] = ConnectionType };
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
    public static JsonObject SerializeConfig(SessionConfig config)
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

        return obj;
    }

    /// <summary>Rebuilds a fresh <see cref="SessionConfig"/> from the forwarded scalar fields.</summary>
    public static SessionConfig DeserializeSessionConfig(JsonElement configElement)
    {
        var config = new SessionConfig();
        ApplyBaseConfig(config, configElement);
        return config;
    }

    /// <summary>Rebuilds a fresh <see cref="ResumeSessionConfig"/> from the forwarded scalar fields.</summary>
    public static ResumeSessionConfig DeserializeResumeSessionConfig(JsonElement configElement)
    {
        var config = new ResumeSessionConfig();
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
    }
}
