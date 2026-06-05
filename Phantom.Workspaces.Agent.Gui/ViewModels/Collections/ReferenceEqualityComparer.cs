using System.Runtime.CompilerServices;

namespace Phantom.Workspaces.Agent.Gui.ViewModels.Collections;

internal sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T>
    where T : class
{
    public static ReferenceEqualityComparer<T> Instance { get; } = new();

    public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

    public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
}
