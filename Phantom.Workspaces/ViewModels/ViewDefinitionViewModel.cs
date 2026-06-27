namespace Phantom.Workspaces.ViewModels;

public sealed class ViewDefinitionViewModel : ViewModelBase
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    public required string IconGlyph { get; init; }

    public bool IsEntityBrowser { get; init; }

    public SubscribedEntityViewModel? ViewEntity { get; init; }
}
