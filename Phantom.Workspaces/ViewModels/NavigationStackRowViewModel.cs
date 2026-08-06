namespace Phantom.Workspaces.ViewModels;

public sealed class NavigationStackRowViewModel : ViewModelBase
{
    private bool isSelected;

    public required string TabTitle { get; init; }
    public string? WorkspaceName { get; init; }
    public bool IsRunning { get; init; }
    public bool IsInteresting { get; init; }

    /// <summary>
    /// The row's "!" attention indicator. Navigation-stack rows have no read state, so this simply
    /// mirrors <see cref="IsInteresting"/>. Shared with the notifications view, whose rows clear the
    /// indicator once read.
    /// </summary>
    public bool ShowsAttentionIndicator => this.IsInteresting;

    public bool IsSelected
    {
        get => this.isSelected;
        set => this.SetProperty(ref this.isSelected, value);
    }
}
