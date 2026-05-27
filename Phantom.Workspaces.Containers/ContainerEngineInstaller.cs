namespace Phantom.Workspaces.Containers;

public abstract class ContainerEngineInstaller
{
    public abstract bool Usable { get; }

    public abstract void Configure();
}
