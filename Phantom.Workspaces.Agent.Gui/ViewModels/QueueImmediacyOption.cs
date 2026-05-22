using Phantom.Workspaces.Llm;
using Avalonia.Media;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed record QueueImmediacyOption(AgentInputQueueImmediacy Value, string Label)
{
    public static readonly IReadOnlyList<QueueImmediacyOption> All =
    [
        new QueueImmediacyOption(AgentInputQueueImmediacy.Immediate, "immediate"),
        new QueueImmediacyOption(AgentInputQueueImmediacy.Queue, "queued"),
        new QueueImmediacyOption(AgentInputQueueImmediacy.Held, "held"),
    ];

    public IBrush Background => this.Value switch
    {
        AgentInputQueueImmediacy.Immediate => new SolidColorBrush(Color.Parse("#1F7A44")),
        AgentInputQueueImmediacy.Queue => new SolidColorBrush(Color.Parse("#295C8A")),
        AgentInputQueueImmediacy.Held => new SolidColorBrush(Color.Parse("#7C5A12")),
        _ => new SolidColorBrush(Color.Parse("#444444")),
    };

    public IBrush BorderBrush => this.Value switch
    {
        AgentInputQueueImmediacy.Immediate => new SolidColorBrush(Color.Parse("#2A9A58")),
        AgentInputQueueImmediacy.Queue => new SolidColorBrush(Color.Parse("#3D77AF")),
        AgentInputQueueImmediacy.Held => new SolidColorBrush(Color.Parse("#A57A18")),
        _ => new SolidColorBrush(Color.Parse("#666666")),
    };

    public IBrush Foreground => Brushes.White;
}
