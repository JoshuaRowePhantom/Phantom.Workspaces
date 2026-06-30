using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Services;

/// <summary>
/// Finds agent-session entities that should be auto-resumed on executor startup.
/// </summary>
public static class AutoResumeService
{
    /// <summary>
    /// The default resume prompt used when an agent-session has auto-resume enabled
    /// but no custom <c>resume-prompt</c> was specified.
    /// </summary>
    public const string DefaultResumePrompt =
        "You were interrupted and restarted. Continue where you left off.";

    /// <summary>
    /// Reads the <c>auto-resume</c> configuration from an entity's JSON data, or returns
    /// <see langword="null"/> if the field is absent or invalid.
    /// </summary>
    public static AutoResumeSettings? ReadFromEntityData(JsonElement? entityData)
    {
        if (entityData is not { } data
            || !data.TryGetProperty("auto-resume", out var autoResumeElement)
            || autoResumeElement.ValueKind != JsonValueKind.Object
            || !autoResumeElement.TryGetProperty("trusted-executor", out var executorElement)
            || executorElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(executorElement.GetString()))
        {
            return null;
        }

        var resumePrompt = autoResumeElement.TryGetProperty("resume-prompt", out var promptElement)
            && promptElement.ValueKind == JsonValueKind.String
            ? promptElement.GetString()
            : null;

        return new AutoResumeSettings
        {
            TrustedExecutor = executorElement.GetString()!,
            ResumePrompt = resumePrompt,
        };
    }

    /// <summary>
    /// Queries all <c>agent-session</c> entities from <paramref name="dataAccessLayer"/> and
    /// returns those whose <c>auto-resume.trusted-executor</c> matches
    /// <paramref name="executorIdentifier"/>.
    /// </summary>
    /// <param name="dataAccessLayer">The data access layer to query.</param>
    /// <param name="executorIdentifier">
    /// The identifier of the executor to match against.
    /// Typically <see cref="Llm.Trust.TrustProfile.LocalClientInstance"/> (<c>"."</c>) for the
    /// local executor, or the local profile entity-id UUID string when the session was created
    /// with an explicit owning profile.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<IReadOnlyList<AutoResumeSessionInfo>> FindMatchingSessionsAsync(
        IDataAccessLayer dataAccessLayer,
        string executorIdentifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataAccessLayer);
        ArgumentNullException.ThrowIfNull(executorIdentifier);

        var queryResult = await dataAccessLayer.QueryAsync(
            new QueryRequest
            {
                Clauses =
                [
                    new TopLevelQueryClause
                    {
                        ClauseIdentifier = new QueryClauseIdentifier("agent-sessions"),
                        Clause = new EntityTypeQueryClause
                        {
                            EntityTypeNames = new EntityTypeNameSet(["agent-session"]),
                        },
                    },
                ],
                Timestamps = [null],
            },
            cancellationToken).ConfigureAwait(false);

        var results = new List<AutoResumeSessionInfo>();
        foreach (var batch in queryResult.Batches)
        {
            foreach (var entity in batch.Entities)
            {
                if (entity.Data is not { } entityData)
                {
                    continue;
                }

                var autoResume = ReadFromEntityData(entityData);
                if (autoResume is null)
                {
                    continue;
                }

                if (!string.Equals(autoResume.TrustedExecutor, executorIdentifier, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!entityData.TryGetProperty("agent-session-id", out var sessionIdElement)
                    || sessionIdElement.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(sessionIdElement.GetString()))
                {
                    continue;
                }

                var effectivePrompt = string.IsNullOrWhiteSpace(autoResume.ResumePrompt)
                    ? DefaultResumePrompt
                    : autoResume.ResumePrompt;

                results.Add(new AutoResumeSessionInfo(
                    entity.EntityId,
                    sessionIdElement.GetString()!,
                    effectivePrompt));
            }
        }

        return results;
    }
}

/// <summary>
/// Describes a single agent-session that should be auto-resumed on executor startup.
/// </summary>
public sealed record AutoResumeSessionInfo(
    EntityId EntityId,
    string AgentSessionId,
    string ResumePrompt);
