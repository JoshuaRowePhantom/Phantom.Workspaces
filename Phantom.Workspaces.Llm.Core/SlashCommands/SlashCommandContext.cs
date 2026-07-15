using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Llm.SlashCommands;

/// <summary>
/// Context passed to every slash command handler at execution time.
/// </summary>
public sealed record SlashCommandContext
{
    /// <summary>The live <see cref="AgentChat"/> instance.</summary>
    public required AgentChat AgentChat { get; init; }

    /// <summary>
    /// The workspace entity id for the <c>agent-session</c> entity, when the session is persisted.
    /// </summary>
    public string? AgentSessionEntityId { get; init; }

    /// <summary>
    /// The current parameter-values for the agent session, if available.
    /// These are the values that feed into parameter substitution when the session definition is built.
    /// </summary>
    public IReadOnlyDictionary<string, string>? CurrentParameterValues { get; init; }

    /// <summary>
    /// Persists updated parameter-values back to the agent-session entity.
    /// Invoked by commands that modify session parameters (e.g. /working-directory).
    /// May be <see langword="null"/> when the session entity is not persisted or the data
    /// access layer is not available.
    /// </summary>
    public Func<IReadOnlyDictionary<string, string>, CancellationToken, Task>? UpdateParameterValuesAsync { get; init; }

    /// <summary>Renames the current session entity and tab title.</summary>
    public Func<string, CancellationToken, Task>? RenameSessionAsync { get; init; }

    /// <summary>Replaces the current tab with a newly cloned session.</summary>
    public Func<CancellationToken, Task>? ReplaceWithCloneAsync { get; init; }

    /// <summary>Opens a newly cloned session in a new tab.</summary>
    public Func<CancellationToken, Task>? OpenCloneInNewTabAsync { get; init; }

    /// <summary>Sets the current tab title only, without persisting the entity display-name.</summary>
    public Func<string, CancellationToken, Task>? SetTabTitleAsync { get; init; }

    /// <summary>
    /// The identifier of the trusted executor for this session context, if available.
    /// Typically <c>"."</c> for the local executor.
    /// May be <see langword="null"/> when the executor context is not available.
    /// </summary>
    public string? TrustedExecutorIdentifier { get; init; }

    /// <summary>
    /// The current <see cref="AutoResumeSettings"/> for the agent-session entity, if auto-resume
    /// is currently enabled. <see langword="null"/> when auto-resume is disabled or unavailable.
    /// </summary>
    public AutoResumeSettings? CurrentAutoResume { get; init; }

    /// <summary>
    /// Persists updated <see cref="AutoResumeSettings"/> back to the agent-session entity.
    /// Pass <see langword="null"/> to disable auto-resume.
    /// May be <see langword="null"/> when the session entity is not persisted or the data
    /// access layer is not available.
    /// </summary>
    public Func<AutoResumeSettings?, CancellationToken, Task>? UpdateAutoResumeAsync { get; init; }
}
