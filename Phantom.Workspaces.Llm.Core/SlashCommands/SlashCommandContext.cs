using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

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
}
