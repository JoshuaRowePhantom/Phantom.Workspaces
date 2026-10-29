using System;
using System.Collections.Generic;
using System.Text.Json;
using Phantom.Workspaces.Services.Logging;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Builds the bounded, protected-aware operational snapshot that the session-loading boundaries
/// (<see cref="OpenAgentSessionShortcutHandler"/> and <see cref="AgentManifestSessionLauncher"/>)
/// attach to their detailed failure records. Ordinary request/identity/timing/configuration values
/// are preserved; protected values render a marker and credential fragments are scrubbed. Snapshot
/// construction is defensive: a formatting or redaction failure yields a marker rather than throwing,
/// so it can never mask the original loading failure, the friendly Failed publication, or cleanup.
/// </summary>
internal static class SessionLoadingDiagnostics
{
    public static string CaptureOperationalSnapshot(
        string stage,
        string attempt,
        string sessionEntityId,
        string agentSessionId,
        string ownerProfileEntityId,
        double elapsedMs,
        JsonElement? entityData,
        string? diagnosticId = null,
        string? failureReason = null)
    {
        try
        {
            var context = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["phase"] = stage,
                ["attempt-id"] = attempt,
                ["session-entity-id"] = sessionEntityId,
                ["agent-session-id"] = agentSessionId,
                ["owner-profile-entity-id"] = ownerProfileEntityId,
                ["total-elapsed-ms"] = elapsedMs,
                ["failure-category"] = failureReason,
                ["diagnostic-id"] = diagnosticId,
                ["runtime-snapshot"] = entityData,
            };

            return DiagnosticSnapshot.Capture(context);
        }
        catch (Exception exception)
        {
            // A diagnostic/redaction failure must never propagate out of the loading boundary.
            return $"[operational-snapshot-failed:{exception.GetType().Name}]";
        }
    }
}
