namespace Phantom.Workspaces.ViewModels;

public sealed record ShortcutImageContent : ShortcutContent
{
    public ShortcutImageContent(
        ShortcutImageResource resource)
    {
        this.Resource = resource;
    }

    public ShortcutImageResource Resource { get; }
}
