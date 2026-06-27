namespace Phantom.Workspaces.Agent.Gui.ViewModels.Visualization;

/// <summary>
/// Returned by <see cref="IToolVisualizerFactory.Visualize"/> to render the content as an
/// expanded <c>&lt;details&gt;</c> element: <paramref name="Label"/> becomes the
/// <c>&lt;summary&gt;</c> and <paramref name="HtmlBody"/> (when non-null) is injected as the body.
/// </summary>
public sealed record Summary(string Label, string? HtmlBody = null);
