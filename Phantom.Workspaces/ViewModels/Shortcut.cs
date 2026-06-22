using System;

namespace Phantom.Workspaces.ViewModels;

public sealed class Shortcut : IEquatable<Shortcut>
{
    public Shortcut(
        string name,
        string label)
    {
        this.Name = name;
        this.Label = label;
    }

    public static Shortcut Open { get; } = new("Open", "↗");
    public static Shortcut Edit { get; } = new("Edit", "✎");
    public static Shortcut Json { get; } = new("Json", "{}");
    public static Shortcut Delete { get; } = new("Delete", "🗑");
    public static Shortcut StartAgentSession { get; } = new("StartAgentSession", "🤖");
    public static Shortcut StartShell { get; } = new("StartShell", "💻");

    public string Name { get; }

    public string Label { get; }

    public string HoverText => this.Name switch
    {
        "Open" => "Open entity",
        "Edit" => "Edit entity",
        "Json" => "Toggle raw JSON view",
        "Delete" => "Delete entity",
        "StartAgentSession" => "Start agent session",
        "StartShell" => "Start shell",
        _ => this.Name,
    };

    public static bool operator ==(
        Shortcut? left,
        Shortcut? right)
    {
        return Equals(left, right);
    }

    public static bool operator !=(
        Shortcut? left,
        Shortcut? right)
    {
        return !Equals(left, right);
    }

    public bool Equals(
        Shortcut? other)
    {
        return other is not null
            && string.Equals(this.Name, other.Name, StringComparison.Ordinal)
            && string.Equals(this.Label, other.Label, StringComparison.Ordinal);
    }

    public override bool Equals(
        object? obj)
    {
        return obj is Shortcut other && this.Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(this.Name, this.Label);
    }
}
