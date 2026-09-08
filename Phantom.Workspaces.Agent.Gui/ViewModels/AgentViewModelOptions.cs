using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

/// <summary>
/// Named-initialiser construction options for <see cref="AgentViewModel"/> (issue #1485).
/// Required members are marked <c>required init</c>; optional members carry explicit defaults so
/// call sites can construct with a single object initialiser.
/// </summary>
public sealed record AgentViewModelOptions
{
    /// <summary>The common-surface chat instance backing the view (issue #1485).</summary>
    public required IAgentChat AgentChat { get; init; }

    /// <summary>Human-facing display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Human-facing description.</summary>
    public required string Description { get; init; }

    /// <summary>Logger factory used to create view-scoped loggers.</summary>
    public required ObservableLoggerFactory LoggerFactory { get; init; }

    /// <summary>Foreground scheduler onto which UI-affine continuations are marshaled.</summary>
    public required TaskScheduler ForegroundScheduler { get; init; }

    /// <summary>The parent view-model when this instance represents a sub-agent editor.</summary>
    public AgentViewModel? ParentAgentViewModel { get; init; } = null;
}
