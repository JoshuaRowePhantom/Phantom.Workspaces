namespace Phantom.Workspaces.Services.TabCommands;

public interface ITabCommandProvider
{
    IReadOnlyList<TabCommandDescriptor> GetCommands(TabCommandContext context);

    event EventHandler? CommandsChanged;
}

internal sealed class EmptyTabCommandProvider : ITabCommandProvider
{
    internal static EmptyTabCommandProvider Instance { get; } = new();

    private EmptyTabCommandProvider()
    {
    }

    public IReadOnlyList<TabCommandDescriptor> GetCommands(TabCommandContext context)
        => [];

    public event EventHandler? CommandsChanged
    {
        add { }
        remove { }
    }
}
