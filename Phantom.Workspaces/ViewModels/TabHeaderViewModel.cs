namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Base header model for workspace tabs. When set on <see cref="WorkspaceTabViewModel.TabHeader"/>,
/// overrides the plain-string <see cref="WorkspaceTabViewModel.Title"/> rendering in the tab strip.
/// </summary>
public class TabHeaderViewModel : ViewModelBase
{
    private string title = string.Empty;

    public required string Title
    {
        get => this.title;
        set => this.SetProperty(ref this.title, value);
    }
}

/// <summary>
/// A <see cref="TabHeaderViewModel"/> that prepends a glyph icon before the tab title.
/// </summary>
public sealed class IconTabHeaderViewModel : TabHeaderViewModel
{
    /// <summary>The icon glyph string (e.g. "🧠") displayed before the title.</summary>
    public required string Icon { get; init; }
}
