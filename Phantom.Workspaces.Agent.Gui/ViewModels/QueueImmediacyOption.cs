using Phantom.Workspaces.Llm;
using Avalonia.Media;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed record QueueImmediacyOption(AgentInputQueueImmediacy Value, string Label)
{
    public IBrush Background => this.Value switch
    {
        AgentInputQueueImmediacy.Immediate => new SolidColorBrush(Color.Parse("#1A6B38")),
        AgentInputQueueImmediacy.Queue => new SolidColorBrush(Color.Parse("#7A5B0A")),
        AgentInputQueueImmediacy.Held => new SolidColorBrush(Color.Parse("#8B1A1A")),
        _ => Brushes.Gray,
    };

    public IBrush BorderBrush => this.Value switch
    {
        AgentInputQueueImmediacy.Immediate => new SolidColorBrush(Color.Parse("#2A9D56")),
        AgentInputQueueImmediacy.Queue => new SolidColorBrush(Color.Parse("#B8870F")),
        AgentInputQueueImmediacy.Held => new SolidColorBrush(Color.Parse("#C0393B")),
        _ => Brushes.DarkGray,
    };

    public IBrush Foreground => Brushes.White;

    public string GlyphText => this.Value switch
    {
        AgentInputQueueImmediacy.Immediate => "⏩",
        AgentInputQueueImmediacy.Queue => "▶",
        AgentInputQueueImmediacy.Held => "⏸",
        _ => string.Empty,
    };

    public static readonly IReadOnlyList<QueueImmediacyOption> All =
    [
        new QueueImmediacyOption(AgentInputQueueImmediacy.Immediate, "immediate"),
        new QueueImmediacyOption(AgentInputQueueImmediacy.Queue, "queued"),
        new QueueImmediacyOption(AgentInputQueueImmediacy.Held, "held"),
    ];
}
