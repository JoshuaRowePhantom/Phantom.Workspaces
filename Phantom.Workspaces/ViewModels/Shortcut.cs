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

    public static Shortcut Open { get; } = new("Open", "Open");

    public string Name { get; }

    public string Label { get; }

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
