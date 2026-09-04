namespace Phantom.Workspaces.Containers;

public abstract class ContainerEngine : IAsyncDisposable
{
    public abstract ValueTask<bool> UsableAsync(
        CancellationToken cancellationToken = default);

    public abstract ValueTask CreateAsync(
        ContainerDefinition definition,
        CancellationToken cancellationToken = default);

    public abstract ValueTask PullAsync(
        string imageName,
        CancellationToken cancellationToken = default);

    public abstract ValueTask StartAsync(
        string containerName,
        CancellationToken cancellationToken = default);

    public abstract ValueTask StopAsync(
        string containerName,
        CancellationToken cancellationToken = default);

    public abstract ValueTask DestroyAsync(
        string containerName,
        CancellationToken cancellationToken = default);

    public virtual ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
