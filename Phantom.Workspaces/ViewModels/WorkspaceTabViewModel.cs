namespace Phantom.Workspaces.ViewModels;

public abstract class WorkspaceTabViewModel : ViewModelBase
{
    private string title = string.Empty;
    private string? tabTooltip;
    private TabHeaderViewModel? tabHeader;

    public required string Id { get; init; }

    public required string Title
    {
        get => this.title;
        set
        {
            if (this.SetProperty(ref this.title, value) && this.tabHeader is not null)
            {
                this.tabHeader.Title = value;
            }
        }
    }

    public string? TabTooltip
    {
        get => this.tabTooltip;
        set => this.SetProperty(ref this.tabTooltip, value);
    }

    /// <summary>
    /// Optional rich header model. When non-null, the tab strip renders this instead of the plain
    /// <see cref="Title"/> string. Set to an <see cref="IconTabHeaderViewModel"/> to add a glyph.
    /// When null the tab falls back to plain-text rendering.
    /// </summary>
    public TabHeaderViewModel? TabHeader
    {
        get => this.tabHeader;
        set => this.SetProperty(ref this.tabHeader, value);
    }

    public string DockRegion { get; init; } = "full";

    public SubscribedEntityViewModel? Entity { get; init; }
}
