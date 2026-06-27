namespace Phantom.Workspaces.Agent.Gui.ViewModels.Visualization;

/// <summary>
/// Evaluates a sequence of <see cref="IToolVisualizerFactory"/> instances in registration order
/// and returns the first non-null visualization result. Returns <see langword="null"/> when no
/// factory handles the content.
/// </summary>
public sealed class CompositeToolVisualizerFactory : IToolVisualizerFactory
{
    private readonly IReadOnlyList<IToolVisualizerFactory> factories;

    public CompositeToolVisualizerFactory(IReadOnlyList<IToolVisualizerFactory> factories)
    {
        ArgumentNullException.ThrowIfNull(factories);
        this.factories = factories;
    }

    public static IToolVisualizerFactory Combine(params IToolVisualizerFactory[] factories)
        => new CompositeToolVisualizerFactory(factories);

    public object? Visualize(ToolVisualizationContext context)
    {
        foreach (var factory in this.factories)
        {
            var result = factory.Visualize(context);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }
}
