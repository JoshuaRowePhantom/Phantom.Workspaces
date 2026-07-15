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
    public static Shortcut OpenWorkspace { get; } = new("OpenWorkspace", "🗔");
    public static Shortcut Json { get; } = new("Json", "{}");
    public static Shortcut Delete { get; } = new("Delete", "🗑");
    public static Shortcut StartAgentSession { get; } = new("StartAgentSession", "🤖");
    public static Shortcut StartShell { get; } = new("StartShell", "💻");
    public static Shortcut Edit { get; } = new("Edit", "✏️");
    public static Shortcut Clone { get; } = new("Clone", "⧉");
    public static Shortcut Review { get; } = new("Review", "±");
    public static Shortcut VsCode { get; } = new("VsCode", "⌨");
    public static Shortcut VsCodeWeb { get; } = new("VsCodeWeb", "🌐");

    public string Name { get; }

    public string Label { get; }

    public string HoverText => this.Name switch
    {
        "Open" => "Open entity",
        "OpenWorkspace" => "Open associated workspace",
        "Json" => "Toggle raw JSON view",
        "Delete" => "Delete entity",
        "StartAgentSession" => "Start agent session",
        "StartShell" => "Start shell",
        "Edit" => "Edit entity",
        "Clone" => "Clone entity",
        "Review" => "Review changes",
        "VsCode" => "Open in VS Code",
        "VsCodeWeb" => "Open in VS Code Web",
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
