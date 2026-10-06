using System.Text;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm;

/// <summary>Embeds SDK correlation keys in the already-persisted child session id.</summary>
internal static class CopilotSubAgentIdentity
{
    private const string Prefix = "copilot-child-";

    internal static AgentSessionId Create(string agentId, string? toolCallId) =>
        new(Prefix + Guid.NewGuid().ToString("n") + "." + Encode(agentId) + "." + Encode(toolCallId ?? ""));

    internal static bool TryRead(string sessionId, out string agentId, out string toolCallId)
    {
        agentId = toolCallId = string.Empty;
        if (!sessionId.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var parts = sessionId[Prefix.Length..].Split('.');
        if (parts.Length != 3 || !Guid.TryParseExact(parts[0], "n", out _)) return false;
        try
        {
            agentId = Decode(parts[1]);
            toolCallId = Decode(parts[2]);
            return agentId.Length > 0 || toolCallId.Length > 0;
        }
        catch (FormatException) { return false; }
    }

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Decode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        return Encoding.UTF8.GetString(Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '=')));
    }
}
