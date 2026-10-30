using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Services;

internal static class SessionIdentityTitlePolicy
{
    internal static string ResolveFallback(
        AgentSessionId sessionId,
        params string?[] candidateNames)
    {
        foreach (var candidateName in candidateNames)
        {
            if (!string.IsNullOrWhiteSpace(candidateName))
            {
                return candidateName;
            }
        }

        var identifier = sessionId.Value;
        if (string.IsNullOrEmpty(identifier))
        {
            return "Agent session (unknown)";
        }

        var shortIdentifier = identifier.Length <= 8 ? identifier : identifier[..8];
        return $"Agent session {shortIdentifier}";
    }
}
