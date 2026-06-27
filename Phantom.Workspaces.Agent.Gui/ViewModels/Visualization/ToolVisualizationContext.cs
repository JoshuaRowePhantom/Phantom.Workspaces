using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Agent.Gui.ViewModels.Visualization;

/// <summary>
/// Input context passed to <see cref="IToolVisualizerFactory.Visualize"/>. Carries the content
/// item to be visualized and any additional ambient context the factory may inspect.
/// </summary>
public sealed record ToolVisualizationContext(AIContent Content);
