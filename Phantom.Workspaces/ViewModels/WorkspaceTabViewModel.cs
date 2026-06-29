namespace Phantom.Workspaces.ViewModels;

public abstract class WorkspaceTabViewModel : ViewModelBase
{
    public event EventHandler? FocusPrimaryControlRequested;

    public virtual void RequestFocusPrimaryControl() =>
        FocusPrimaryControlRequested?.Invoke(this, EventArgs.Empty);

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
    /// Optional rich header model. When non-null, its <see cref="TabHeaderViewModel.Items"/> are
    /// merged into the <see cref="WorkspaceDocument.EffectiveTabHeader"/> shown in the tab strip.
    /// Use <see cref="TabHeaderViewModel.WithIcon"/> to set an icon glyph.
    /// </summary>
    public TabHeaderViewModel? TabHeader
    {
        get => this.tabHeader;
        set => this.SetProperty(ref this.tabHeader, value);
    }

    public string DockRegion { get; init; } = "full";

    public SubscribedEntityViewModel? Entity { get; init; }

    /// <summary>
    /// The status item for this tab, or null if the tab has no meaningful running/error state.
    /// Overridden by tab types that run background work (e.g. agent sessions).
    /// </summary>
    public virtual IStatusItem? TabStatus => null;
}
