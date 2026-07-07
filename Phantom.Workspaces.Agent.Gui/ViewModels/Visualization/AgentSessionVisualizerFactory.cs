using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Agent.Gui.ViewModels.Visualization;

/// <summary>
/// Produces visualizations for <c>agent_session_*</c> tools, which are used by parent agents
/// to create, manage, and monitor subordinate agent sessions (subagents).
/// </summary>
public sealed class AgentSessionVisualizerFactory : IToolVisualizerFactory
{
    private static readonly HashSet<string> AgentSessionToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "agent_session_create",
        "agent_session_list",
        "agent_session_get",
        "agent_session_send",
        "agent_session_stop",
        "agent_session_read_events",
        "agent_session_wait",
        "agent_session_on_complete",
        "agent_session_acquire",
    };

    public object? Visualize(ToolVisualizationContext context)
    {
        return context.Content switch
        {
            FunctionCallContent call when AgentSessionToolNames.Contains(call.Name ?? string.Empty)
                => VisualizeCall(call),
            FunctionResultContent => null,
            _ => null,
        };
    }

    private static object? VisualizeCall(FunctionCallContent call)
    {
        var label = BuildCallLabel(call);
        return new Summary(label, null);
    }

    private static string BuildCallLabel(FunctionCallContent call)
    {
        var args = call.Arguments;

        return call.Name switch
        {
            "agent_session_create" => BuildCreateLabel(args),
            "agent_session_send" => BuildSendLabel(args),
            "agent_session_stop" => BuildStopLabel(args),
            "agent_session_wait" => BuildWaitLabel(args),
            "agent_session_get" => BuildGetLabel(args),
            "agent_session_list" => "agent_session_list",
            "agent_session_read_events" => BuildReadEventsLabel(args),
            "agent_session_on_complete" => BuildOnCompleteLabel(args),
            "agent_session_acquire" => BuildAcquireLabel(args),
            _ => call.Name ?? "agent_session",
        };
    }

    private static string BuildCreateLabel(IDictionary<string, object?>? args)
    {
        if (args is null || args.Count == 0)
            return "+ subagent (parent definition)";

        if (TryGetStringArg(args, "initial_message", out var msg))
            return $"+ subagent → \"{Truncate(msg!, 40)}\"";

        return "+ subagent";
    }

    private static string BuildSendLabel(IDictionary<string, object?>? args)
    {
        if (args is null)
            return "→ subagent";

        var sessionId = GetShortSessionId(args);
        if (TryGetStringArg(args, "text", out var text))
            return $"→ {sessionId}: \"{Truncate(text!, 50)}\"";

        return $"→ {sessionId}";
    }

    private static string BuildStopLabel(IDictionary<string, object?>? args)
    {
        if (args is null)
            return "■ subagent stopped";

        var sessionId = GetShortSessionId(args);
        if (TryGetBoolArg(args, "dispose", out var dispose) && dispose)
            return $"■ {sessionId} stopped (disposed)";

        return $"■ {sessionId} stopped";
    }

    private static string BuildWaitLabel(IDictionary<string, object?>? args)
    {
        if (args is null)
            return "⏳ waiting for subagent";

        var sessionId = GetShortSessionId(args);
        if (TryGetIntArg(args, "timeout_seconds", out var timeout))
            return $"⏳ waiting for {sessionId} ({timeout}s)";

        return $"⏳ waiting for {sessionId}";
    }

    private static string BuildGetLabel(IDictionary<string, object?>? args)
    {
        var sessionId = GetShortSessionId(args);
        return $"agent_session_get {sessionId}";
    }

    private static string BuildReadEventsLabel(IDictionary<string, object?>? args)
    {
        var sessionId = GetShortSessionId(args);
        return $"agent_session_read_events {sessionId}";
    }

    private static string BuildOnCompleteLabel(IDictionary<string, object?>? args)
    {
        var sessionId = GetShortSessionId(args);
        return $"agent_session_on_complete {sessionId}";
    }

    private static string BuildAcquireLabel(IDictionary<string, object?>? args)
    {
        var sessionId = GetShortSessionId(args);
        return $"agent_session_acquire {sessionId}";
    }

    private static string GetShortSessionId(IDictionary<string, object?>? args)
    {
        if (args is null)
            return "session";

        if (!TryGetStringArg(args, "session_id", out var sessionId) || sessionId is null)
            return "session";

        if (sessionId == ".")
            return "self";

        return sessionId.Length > 8 ? sessionId[..8] : sessionId;
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
            return value;

        return value[..(maxLength - 1)] + "…";
    }

    private static bool TryGetStringArg(IDictionary<string, object?> args, string key, out string? value)
    {
        if (args.TryGetValue(key, out var raw))
        {
            value = raw switch
            {
                string s => s,
                JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
                _ => raw?.ToString(),
            };
            return value is not null;
        }

        value = null;
        return false;
    }

    private static bool TryGetBoolArg(IDictionary<string, object?> args, string key, out bool value)
    {
        if (args.TryGetValue(key, out var raw))
        {
            value = raw switch
            {
                bool b => b,
                JsonElement { ValueKind: JsonValueKind.True } => true,
                JsonElement { ValueKind: JsonValueKind.False } => false,
                string s when bool.TryParse(s, out var parsed) => parsed,
                _ => false,
            };
            return true;
        }

        value = false;
        return false;
    }

    private static bool TryGetIntArg(IDictionary<string, object?> args, string key, out int value)
    {
        if (args.TryGetValue(key, out var raw))
        {
            value = raw switch
            {
                int i => i,
                long l => (int)l,
                JsonElement { ValueKind: JsonValueKind.Number } e when e.TryGetInt32(out var n) => n,
                _ => 0,
            };
            return value != 0;
        }

        value = 0;
        return false;
    }
}
