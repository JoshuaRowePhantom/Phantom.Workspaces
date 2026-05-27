namespace Phantom.Workspaces.Containers;

public abstract class ContainerEngineInstaller
{
    public virtual ValueTask<bool> Usable()
    {
        return ValueTask.FromResult(true);
    }

    public virtual ValueTask Configure()
    {
        throw new NotImplementedException();
    }
}
